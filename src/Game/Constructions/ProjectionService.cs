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
                case "create_template":
                    var spawner3 = Spawner(session) ?? throw new InvalidOperationException("spawner_unavailable");
                    return CreateTemplate(session, projectionsComponent, spawner3, epoch, r, now);
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

    // Hand-built template: an N x N steel plate made from the donor grid's most
    // common (armor) block. No pre-made grid required — the bundle is assembled
    // programmatically from a single cloned block OB.
    private ProjectionStatus CreateTemplate(Session session, GridProjectionsSessionComponent projections,
        IEntitySpawner spawner, string epoch, ProjectionRequest r, long now)
    {
        var donor = FindGrid(session, r.GridId) ?? throw new InvalidOperationException("donor_grid_not_found");
        var host = string.IsNullOrWhiteSpace(r.TargetGridId) ? donor : FindGrid(session, r.TargetGridId!) ?? donor;
        int n = Math.Clamp(r.Count > 0 ? r.Count : 3, 1, 8);
        // 1. Serialize the donor grid to find an armor block OB + its local step.
        var serializer = session.Scene.EntitySerializer;
        var allocate = typeof(Keen.VRage.DCS.Builders.EntitySerializer)
            .GetMethod("AllocateEmptyEOBFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var ob = (Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder)allocate!.Invoke(serializer, new object[] { donor.Entity })!;
        serializer.Serialize(donor.Entity, ob);
        var bundle = new EntityBundle();
        bundle.Add(ob, donor.Entity, true);
        var rootOb = bundle.RootBuilders[0];
        var kids = Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilderExtensions.TryGetDirectChilds(rootOb);
        if (kids.Length == 0) throw new InvalidOperationException("donor_has_no_blocks");
        // 2. Group child OBs by Definition reference; the most common one is armor.
        var byDefinition = new Dictionary<Keen.VRage.DCS.Definitions.EntityCompositeDefinition, List<Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder>>();
        Keen.VRage.Library.Mathematics.Vector3D? reference = null;
        var positions = new List<Keen.VRage.Library.Mathematics.Vector3D>();
        for (int i = 0; i < kids.Length; i++)
        {
            var key = kids[i].Definition;
            if (!byDefinition.TryGetValue(key!, out var list)) byDefinition[key!] = list = new();
            list.Add(kids[i]);
            foreach (var c in kids[i].ObjectBuilders)
                if (c is Keen.VRage.Core.Game.Components.ChildTransformComponentObjectBuilder ct)
                {
                    var p = ct.Transform.Position;
                    positions.Add(new(p.X, p.Y, p.Z));
                    break;
                }
        }
        // Steel plate: prefer "simple" blocks (no functional component OBs — armor).
        bool Simple(Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder b) =>
            !b.ObjectBuilders.Any(c => c != null && (c.GetType().Name.Contains("Thruster") ||
                c.GetType().Name.Contains("Powerable") || c.GetType().Name.Contains("Gyroscope") ||
                c.GetType().Name.Contains("Drill") || c.GetType().Name.Contains("Connector") ||
                c.GetType().Name.Contains("Battery") || c.GetType().Name.Contains("Reactor") ||
                c.GetType().Name.Contains("Door") || c.GetType().Name.Contains("Cockpit") ||
                c.GetType().Name.Contains("Cargo") || c.GetType().Name.Contains("Conveyor")));
        var simple = byDefinition.Where(kv => kv.Value.Any(Simple)).ToList();
        var armorGroup = (simple.Count > 0
            ? simple.OrderByDescending(kv => kv.Value.Count)
            : byDefinition.OrderByDescending(kv => kv.Value.Count)).First().Value;
        var template = armorGroup.First(Simple);
        try { System.IO.File.AppendAllText(@"C:\SE2GameAPI\template_trace.log",
            $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} armor group={armorGroup.Count} template components=[{string.Join(",", template.ObjectBuilders.Select(c => c?.GetType().Name ?? "null"))}]\n"); } catch { }
        // reference = the template's local position; step = minimal non-zero delta
        foreach (var c in template.ObjectBuilders)
            if (c is Keen.VRage.Core.Game.Components.ChildTransformComponentObjectBuilder ct2)
            { var p = ct2.Transform.Position; reference = new(p.X, p.Y, p.Z); break; }
        if (reference == null) throw new InvalidOperationException("template_block_has_no_transform");
        double step = double.MaxValue;
        for (int a = 0; a < positions.Count && a < 64; a++)
            for (int b = 0; b < positions.Count && b < 64; b++)
            {
                if (a == b) continue;
                var d = Keen.VRage.Library.Mathematics.Vector3D.Abs(positions[a] - positions[b]);
                var min = Math.Min(Math.Min(d.X, d.Y), d.Z);
                if (min > 1e-6) step = Math.Min(step, min);
            }
        if (step == double.MaxValue) step = 0.5; // small-grid fallback
        // 3. Assemble: clean the grid root's children, add N*N clones, THEN bundle.
        var gridOb = rootOb.DeepClone();
        // Reset the root transform to identity: without it TransformInvBundle
        // bakes the donor's world position into every local block coordinate.
        foreach (var c in gridOb.ObjectBuilders)
            if (c is Keen.VRage.Core.Game.Components.WorldTransformComponentObjectBuilder wt)
            { var t = wt.Transform; t.Position = new(0, 0, 0); t.Orientation = Keen.VRage.Library.Mathematics.Quaternion.Identity; wt.Transform = t; }
        foreach (var c in gridOb.ObjectBuilders)
            if (c?.GetType().Name == "HierarchyComponentObjectBuilder")
                try
                {
                    var childrenField = c.GetType().GetField("Children",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var children = childrenField?.GetValue(c);
                    children?.GetType().GetMethod("Clear")?.Invoke(children, null);
                }
                catch { }
        var addDirect = typeof(Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilderExtensions)
            .GetMethods().First(m => m.Name == "AddDirectChild");
        int made = 0;
        double half = (n - 1) / 2.0;
        for (int ix = 0; ix < n; ix++)
            for (int iz = 0; iz < n; iz++)
            {
                var clone = template.DeepClone();
                // Center the plate on the bundle origin: the spawn offset then
                // controls the world position exactly (reference-free).
                // X-Z plane: a horizontal base plate.
                var pos = new Keen.VRage.Library.Mathematics.Vector3D(
                    (ix - half) * step, 0, (iz - half) * step);
                foreach (var c in clone.ObjectBuilders)
                    if (c is Keen.VRage.Core.Game.Components.ChildTransformComponentObjectBuilder ct)
                    {
                        var t = ct.Transform;
                        t.Position = new((float)pos.X, (float)pos.Y, (float)pos.Z);
                        ct.Transform = t;
                    }
                addDirect.Invoke(null, new object[] { gridOb, clone });
                made++;
            }
        var newBundle = new EntityBundle();
        newBundle.Add(gridOb, donor.Entity, true);
        // 4. Prepare + spawn near the host + attach.
        var owner = host.Entity.TryGet<OwnershipComponent>()?.Owner ?? default;
        if (owner == default)
            foreach (var entity in session.GetEntitiesOfType<CubeGridComponent>())
            {
                var g = entity.TryGet<CubeGridComponent>();
                var o = g?.Entity.TryGet<OwnershipComponent>()?.Owner;
                if (o.HasValue && o.Value != default) { owner = o.Value; break; }
            }
        var projectionOb = projections.PrepareGridProjection(newBundle, owner)
            ?? throw new InvalidOperationException("prepare_projection_failed");
        var builder = DEntityBuilder.ForScene(session.Scene);
        var spawned = spawner.SpawnEntity(projectionOb, builder)
            ?? throw new InvalidOperationException("projection_spawn_failed");
        var projection = spawned.TryGet<ProjectionSourceComponent>()
            ?? throw new InvalidOperationException("projection_component_missing");
        projections.AttachProjection(projection, host);
        if (r.Offset is { Length: 3 } shift)
        {
            var target = host.Data.GetWorldTransform();
            target.Position = new Keen.VRage.Library.Mathematics.Vector3D(
                target.Position.X + shift[0], target.Position.Y + shift[1], target.Position.Z + shift[2]);
            Keen.VRage.Core.Game.Data.EntityTransformFunctions.Teleport(spawned.Data, ref target);
        }
        return new ProjectionStatus(r.RequestId, epoch, r.GridId, "complete", null,
            spawned.DEntity.ToString(), 1, made, now);
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
