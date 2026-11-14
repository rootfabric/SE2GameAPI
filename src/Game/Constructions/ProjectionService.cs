using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.Game2.Simulation.GameSystems.Blueprints;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.CoreData;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Physics.Data;
using SE2GameAPI.Redis;
using System.Collections;

namespace SE2GameAPI.Game.Constructions;

// Grid projections ("blueprint ghosts"): serialize an existing grid into an
// EntityBundle, spawn it as a ProjectionSourceComponent entity and attach it
// to a host grid. Materialization turns projected blocks into real blocks
// one by one (the same path the hand welder uses).
internal sealed class ProjectionService
{
    private GridProjectionsSessionComponent? projections;
    private IEntitySpawner? spawner;
    private int generation = -1;

    internal void Reset(int currentGeneration)
    {
        if (generation == currentGeneration) return;
        generation = currentGeneration;
        projections = null;
        spawner = null;
    }

    private static T? ResolveSessionSystem<T>(Session session) where T : class
    {
        try
        {
            var componentsEntity = SE2GameAPI.Game.Voxels.ReflectionVoxelStorage.Get(session, "SessionComponents");
            if (componentsEntity == null) return null;
            var raw = SE2GameAPI.Game.Voxels.ReflectionVoxelStorage.Get(componentsEntity, "Components");
            IEnumerable? list = raw as IEnumerable;
            if (list == null && raw is ICollection collection) list = collection.Cast<object>();
            if (list == null) return null;
            foreach (var component in list)
                if (component is T found) return found;
        }
        catch { }
        return null;
    }

    private GridProjectionsSessionComponent? Projections(Session session)
    {
        if (projections != null) return projections;
        return projections = ResolveSessionSystem<GridProjectionsSessionComponent>(session);
    }

    private IEntitySpawner? Spawner(Session session)
    {
        if (spawner != null) return spawner;
        return spawner = ResolveSessionSystem<IEntitySpawner>(session);
    }

    private static CubeGridComponent? FindGrid(Session session, string gridId)
    {
        foreach (var entity in session.GetEntitiesOfType<CubeGridComponent>())
        {
            var grid = entity.TryGet<CubeGridComponent>();
            if (grid != null && grid.DEntity.ToString() == gridId) return grid;
        }
        return null;
    }

    internal async Task<ProjectionStatus> Run(Session session, string epoch, ProjectionRequest r, long now)
    {
        try
        {
            var projectionsComponent = Projections(session) ?? throw new InvalidOperationException("projections_component_unavailable");
            switch (r.Action)
            {
                case "create":
                    var spawnerComponent = Spawner(session) ?? throw new InvalidOperationException("spawner_unavailable");
                    return await Create(session, projectionsComponent, spawnerComponent, epoch, r, now);
                case "convert_block":
                    var spawner2 = Spawner(session) ?? throw new InvalidOperationException("spawner_unavailable");
                    return ConvertBlock(session, projectionsComponent, spawner2, epoch, r, now);
                case "list": return List(session, epoch, r, now);
                case "materialize": return Materialize(session, projectionsComponent, epoch, r, now);
                default: throw new InvalidOperationException("invalid_action");
            }
        }
        catch (Exception e)
        {
            var trace = e.GetBaseException().StackTrace ?? "";
            var firstFrame = string.Join(" | ", trace.Split('\n').Where(l => l.Contains("SE2GameAPI")).Take(3)).Trim();
            return new ProjectionStatus(r.RequestId, epoch, r.GridId, "failed",
                e.GetBaseException().Message + (firstFrame != "" ? " @ " + firstFrame : ""),
                null, 0, 0, now);
        }
    }

    private async Task<ProjectionStatus> Create(Session session, GridProjectionsSessionComponent projections,
        IEntitySpawner spawner, string epoch, ProjectionRequest r, long now)
    {
        var source = FindGrid(session, r.GridId) ?? throw new InvalidOperationException("source_grid_not_found");
        var target = string.IsNullOrWhiteSpace(r.TargetGridId)
            ? source
            : FindGrid(session, r.TargetGridId) ?? throw new InvalidOperationException("target_grid_not_found");
        // 1. Serialize the whole source grid into a bundle (blueprint copy path).
        var story = BlueprintHelpers.SerializeGrid(source, null);
        var detached = await story;
        var bundle = detached.Value;
        // 2. Convert the bundle into a projection entity builder.
        var owner = source.Entity.TryGet<OwnershipComponent>()?.Owner ?? default;
        var projectionOb = projections.PrepareGridProjection(bundle, owner)
            ?? throw new InvalidOperationException("prepare_projection_failed");
        // 3. Position: an optional offset from the target grid center (world space).
        if (r.Offset is { Length: 3 } offset)
        {
            var targetWt = target.Data.GetWorldTransform();
            var position = new Keen.VRage.Library.Mathematics.Vector3D(
                targetWt.Position.X + offset[0], targetWt.Position.Y + offset[1], targetWt.Position.Z + offset[2]);
            // WorldTransform is a struct: read-copy-write, never reflection SetValue (boxing).
            foreach (var child in projectionOb.ObjectBuilders)
            {
                if (child is not Keen.VRage.Core.Game.Components.WorldTransformComponentObjectBuilder wt) continue;
                var transform = wt.Transform;
                transform.Position = position;
                wt.Transform = transform;
            }
        }
        // 4. Spawn the projection entity and attach it to the host grid.
        var builder = DEntityBuilder.ForScene(session.Scene);
        var spawned = spawner.SpawnEntity(projectionOb, builder)
            ?? throw new InvalidOperationException("projection_spawn_failed");
        var projection = spawned.TryGet<ProjectionSourceComponent>()
            ?? throw new InvalidOperationException("projection_component_missing");
        projections.AttachProjection(projection, target);
        return new ProjectionStatus(r.RequestId, epoch, r.GridId, "complete", null,
            spawned.DEntity.ToString(), 1, 0, now);
    }

    // Mission-style conversion: serialize the WHOLE source grid, spawn its
    // projection at the exact same place, then delete the original. The
    // projection ghost keeps every mount point, ready for block-by-block
    // materialization (the hand welder path).
    private ProjectionStatus ConvertBlock(Session session, GridProjectionsSessionComponent projections,
        IEntitySpawner spawner, string epoch, ProjectionRequest r, long now)
    {
        var grid = FindGrid(session, r.GridId) ?? throw new InvalidOperationException("grid_not_found");
        var gridEntity = grid.Entity;
        void Mark(string step)
        {
            try { System.IO.File.AppendAllText(@"C:\SE2GameAPI\convert_trace.log", DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff") + " " + step + "\n"); } catch { }
        }
        Mark("grid found");
        // 1. Serialize the whole grid into a bundle (same as the mission converter).
        var serializer = session.Scene.EntitySerializer;
        var allocate = typeof(Keen.VRage.DCS.Builders.EntitySerializer)
            .GetMethod("AllocateEmptyEOBFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var ob = (Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder)allocate!.Invoke(serializer, new object[] { gridEntity })!;
        serializer.Serialize(gridEntity, ob);
        Mark("serialized");
        var bundle = new EntityBundle();
        bundle.Add(ob, gridEntity, true);
        Mark("bundled roots=" + bundle.Roots);
        // 2. Prepare + spawn the projection exactly at the grid position.
        // Owner comes from the first block (mission path); grids may carry none.
        Keen.Game2.Simulation.GameSystems.Ownership.IdentityId owner = default;
        var blocks = grid.CubeBlockStorage.GetAllCubeBlocks();
        for (int i = 0; i < blocks.Length; i++)
        {
            if (blocks[i] == null) continue;
            var blockOwner = blocks[i].Entity.TryGet<OwnershipComponent>()?.Owner;
            if (blockOwner.HasValue) { owner = blockOwner.Value; break; }
        }
        Mark("owner: " + owner);
        if (owner == default)
        {
            // Unowned NPC grid: borrow any valid owner in the world (player bases).
            foreach (var entity in session.GetEntitiesOfType<CubeGridComponent>())
            {
                var g = entity.TryGet<CubeGridComponent>();
                if (g == null) continue;
                var o = g.Entity.TryGet<OwnershipComponent>()?.Owner;
                if (o.HasValue && o.Value != default) { owner = o.Value; break; }
            }
            Mark("owner fallback: " + owner);
        }
        var projectionOb = projections.PrepareGridProjection(bundle, owner)
            ?? throw new InvalidOperationException("prepare_projection_failed");
        Mark("prepared");
        // Optional small offset: projected blocks must touch the source grid to
        // have mount points for materialization (partial overlap is fine).
        var builder = DEntityBuilder.ForScene(session.Scene);
        var spawned = spawner.SpawnEntity(projectionOb, builder)
            ?? throw new InvalidOperationException("projection_spawn_failed");
        Mark("spawned");
        var projection = spawned.TryGet<ProjectionSourceComponent>()
            ?? throw new InvalidOperationException("projection_component_missing");
        // Attach to the requested host grid (default: the source grid itself).
        var host = string.IsNullOrWhiteSpace(r.TargetGridId) ? grid : FindGrid(session, r.TargetGridId!) ?? grid;
        projections.AttachProjection(projection, host);
        Mark("attached");
        // The bundle keeps absolute block positions (TransformInvBundle recomputes
        // children), so shifting OBs is useless. Move the spawned projection
        // entity itself: materialization bakes entity transform + local blocks.
        if (r.Offset is { Length: 3 } shift)
        {
            var target = host.Data.GetWorldTransform();
            target.Position = new Keen.VRage.Library.Mathematics.Vector3D(
                target.Position.X + shift[0], target.Position.Y + shift[1], target.Position.Z + shift[2]);
            Keen.VRage.Core.Game.Data.EntityTransformFunctions.Teleport(spawned.Data, ref target);
            Mark("teleported");
        }
        // The source grid stays alive: deleting it tears down the attached
        // projection with it. Materialize from the ghost builds a copy.
        return new ProjectionStatus(r.RequestId, epoch, r.GridId, "complete", null,
            spawned.DEntity.ToString(), 1, 0, now);
    }

    private ProjectionStatus List(Session session, string epoch, ProjectionRequest r, long now)
    {
        // Enumerate all ProjectionSourceComponent entities in the scene.
        var found = new List<string>();
        foreach (var entity in session.GetEntitiesOfType<ProjectionSourceComponent>())
        {
            var p = entity.TryGet<ProjectionSourceComponent>();
            if (p == null) continue;
            found.Add(entity.DEntity.ToString());
        }
        return new ProjectionStatus(r.RequestId, epoch, r.GridId, "complete", null,
            found.Count > 0 ? string.Join(",", found) : null, found.Count, 0, now);
    }

    private ProjectionStatus Materialize(Session session, GridProjectionsSessionComponent projections,
        string epoch, ProjectionRequest r, long now)
    {
        // Find the projection entity by id (r.TargetGridId holds the projection entity id).
        ProjectionSourceComponent? projection = null;
        Keen.VRage.DCS.Components.Entity? projectionEntity = null;
        foreach (var entity in session.GetEntitiesOfType<ProjectionSourceComponent>())
        {
            var p = entity.TryGet<ProjectionSourceComponent>();
            if (p != null && entity.DEntity.ToString() == (r.TargetGridId ?? ""))
            { projection = p; projectionEntity = entity; break; }
        }
        if (projection == null || projectionEntity == null)
            throw new InvalidOperationException("projection_not_found");
        var host = FindGrid(session, r.GridId);
        var owner = host?.Entity.TryGet<OwnershipComponent>()?.Owner ?? default;
        // Walk the projection bundle: root builder -> direct children (blocks).
        var bundle = projection.ProjectionData;
        if (bundle.RootBuilders.Length == 0)
            throw new InvalidOperationException("projection_bundle_empty");
        var rootOb = bundle.RootBuilders[0];
        try { System.IO.File.AppendAllText(@"C:\SE2GameAPI\convert_trace.log",
            $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} materialize: roots={bundle.Roots} rootChildren={Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilderExtensions.TryGetDirectChilds(rootOb).Length}\n"); } catch { }
        int materialized = 0, failures = 0;
        string? firstError = null;
        int limit = r.Count > 0 ? r.Count : int.MaxValue;
        // ChildRef hierarchy: [index in Builders, index in Children span, ...].
        var children = Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilderExtensions
            .TryGetDirectChilds(rootOb);
        for (int i = 0; i < children.Length; i++)
        {
            if (materialized >= limit) break;
            Span<int> hierarchy = stackalloc int[] { 0, i };
            var childRef = new ProjectionSourceComponent.ChildRef(hierarchy);
            try
            {
                var result = projections.TryMaterializeBlock(projection, childRef, owner, true);
                if (result.Item1 == Keen.Game2.Simulation.GameSystems.BlockPlacement.BuildResultEnum.Success) materialized++;
                else { failures++; firstError ??= result.Item1.ToString(); }
            }
            catch (Exception e) { failures++; firstError ??= e.GetBaseException().Message; }
        }
        return new ProjectionStatus(r.RequestId, epoch, r.GridId, "complete",
            failures > 0 ? $"{failures} failed: {firstError ?? "unknown"}" : null,
            projectionEntity.DEntity.ToString(), 1, materialized, now);
    }
}
