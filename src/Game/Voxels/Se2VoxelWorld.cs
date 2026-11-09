using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.CoreData;
namespace SE2GameAPI.Game.Voxels;

// Created/used exclusively after Scene.MoveToDCS(). No background engine access.
internal sealed class Se2VoxelWorld(Session session) : IVoxelWorld
{
    public VoxelPose? FindGrid(string id)
    {
        foreach(var entity in session.GetEntitiesOfType<CubeGridComponent>())
        {
            var grid=entity.Get<CubeGridComponent>();
            if(grid.GridClosing || grid.DEntity.ToString()!=id) continue;
            var p=grid.Data.GetWorldTransform().Position;
            return new(id,grid.DisplayName.EvaluateText(),[p.X,p.Y,p.Z]);
        }
        return null;
    }
    public VoxelCatalog Discover()=>ReflectionVoxelCatalog.Discover(session);
}
