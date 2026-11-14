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
            var spawnerComponent = Spawner(session) ?? throw new InvalidOperationException("spawner_unavailable");
            switch (r.Action)
            {
                case "create": return await Create(session, projectionsComponent, spawnerComponent, epoch, r, now);
                case "list": return List(projectionsComponent, epoch, r, now);
                case "materialize": return Materialize(session, projectionsComponent, epoch, r, now);
                default: throw new InvalidOperationException("invalid_action");
            }
        }
        catch (Exception e)
        {
            return new ProjectionStatus(r.RequestId, epoch, r.GridId, "failed", e.GetBaseException().Message,
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
            // Component builders live in the ObjectBuilders keyed list.
            foreach (var child in projectionOb.ObjectBuilders)
            {
                if (child?.GetType().Name != "WorldTransformComponentObjectBuilder") continue;
                var wtProp = child.GetType().GetProperty("Transform");
                if (wtProp == null || !wtProp.CanWrite) continue;
                var transform = wtProp.GetValue(child);
                var posProp = transform?.GetType().GetProperty("Position");
                if (posProp != null && posProp.CanWrite)
                    posProp.SetValue(transform, position);
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

    private ProjectionStatus List(GridProjectionsSessionComponent projections, string epoch, ProjectionRequest r, long now)
    {
        // AllProjections requires a grid entity; enumerate via the session scene.
        throw new InvalidOperationException("list_not_exposed");
    }

    private ProjectionStatus Materialize(Session session, GridProjectionsSessionComponent projections,
        string epoch, ProjectionRequest r, long now)
    {
        throw new InvalidOperationException("materialize_not_exposed");
    }
}
