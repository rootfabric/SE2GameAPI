using System.Collections.Immutable;
using System.Reflection;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Voxels.Components;
using Keen.VRage.Voxels.Definitions;
using Keen.VRage.Voxels.Storage;
namespace SE2GameAPI.Game.Voxels;

// Typed adapter for the installed VRage.Voxels build. StorageData is a ref struct with
// pointer-backed spans: it cannot be created or read through reflection, so this adapter
// binds at compile time to VoxelStorageComponentBase/IStorage members directly.
internal sealed class TypedVoxelStorage : IVoxelStorage
{
    private readonly VoxelStorageComponentBase component;
    private readonly Func<bool> alive;
    public string Id { get; }
    public object Identity => component;
    public I3 Size { get; }
    public VoxelFrame Frame { get; }
    public string? Revision => null; // no reliable revision member in this build; scans rely on Fresh

    private TypedVoxelStorage(string id, VoxelStorageComponentBase source, I3 size, VoxelFrame frame, Func<bool> isAlive)
    {
        Id=id; component=source; Size=size; Frame=frame; alive=isAlive;
    }
    public static TypedVoxelStorage? TryBind(string id, VoxelStorageComponentBase component, Func<bool> alive)
    {
        var storage=component.Storage;
        if(storage==null) return null; // procedural planet chunks may not be loaded yet
        var s=storage.Size;
        var size=new I3(s.X,s.Y,s.Z);
        if(size.X<1||size.Y<1||size.Z<1) return null;
        V3 P(int x,int y,int z)
        {
            var p=component.StorageToWorldPosition(new Vector3I(x,y,z));
            return new(p.X,p.Y,p.Z);
        }
        var origin=P(0,0,0);
        var frame=new VoxelFrame(origin,P(1,0,0)-origin,P(0,1,0)-origin,P(0,0,1)-origin);
        if(!frame.Valid) return null;
        return new(id,component,size,frame,alive);
    }
    public byte[] Read(I3 min, I3 size)
    {
        if(!alive()) throw new InvalidOperationException("voxel_entity_unloaded");
        if(size.X<=0||size.Y<=0||size.Z<=0||size.X>16||size.Y>16||size.Z>16||
            min.X<0||min.Y<0||min.Z<0||(long)min.X+size.X>Size.X||(long)min.Y+size.Y>Size.Y||(long)min.Z+size.Z>Size.Z)
            throw new ArgumentOutOfRangeException(nameof(size));
        var storage=component.Storage ?? throw new InvalidOperationException("voxel_storage_offloaded");
        var data=new StorageData(new Vector3I(size.X,size.Y,size.Z),StorageDataTypeFlags.Content,false,"SE2GameAPI");
        try
        {
            var flags=VoxelRequestFlags.DefaultRequestFlags;
            storage.ReadRange(ref data,StorageDataTypeFlags.Content,0,
                new Vector3I(min.X,min.Y,min.Z),
                new Vector3I(min.X+size.X-1,min.Y+size.Y-1,min.Z+size.Z-1),ref flags);
            var result=new byte[size.Count]; int i=0;
            for(int z=0;z<size.Z;z++) for(int y=0;y<size.Y;y++) for(int x=0;x<size.X;x++)
                result[i++]=data.Content(x,y,z);
            return result;
        }
        finally { data.Dispose(); }
    }
    public byte[]? ReadMaterials(I3 min, I3 size)
    {
        if(!alive()) return null;
        var processor=MaterialProcessor();
        if(processor==null) return null;
        var storage=component.Storage;
        if(storage==null) return null;
        var data=new StorageData(new Vector3I(size.X,size.Y,size.Z),StorageDataTypeFlags.ContentAndMaterial,false,"SE2GameAPI");
        try
        {
            var flags=VoxelRequestFlags.DefaultRequestFlags;
            storage.ReadRange(ref data,StorageDataTypeFlags.ContentAndMaterial,0,
                new Vector3I(min.X,min.Y,min.Z),
                new Vector3I(min.X+size.X-1,min.Y+size.Y-1,min.Z+size.Z-1),ref flags);
            var result=new byte[size.Count]; int i=0;
            for(int z=0;z<size.Z;z++) for(int y=0;y<size.Y;y++) for(int x=0;x<size.X;x++)
                result[i++]=data.Material(x,y,z);
            return result;
        }
        catch { return null; } // Material channel is optional; content tiles must still publish.
        finally { data.Dispose(); }
    }
    public IReadOnlyDictionary<byte,string>? MaterialLegend
    {
        get
        {
            var processor=MaterialProcessor();
            if(processor==null) return null;
            legend??=BuildLegend(processor);
            return legend.Count>0?legend:null;
        }
    }
    private Dictionary<byte,string>? legend;
    // The getter is not compile-visible on VoxelStorageComponentBase; the backing field is stable.
    private Keen.VRage.Voxels.Definitions.VoxelMaterialDefinitionPostProcessor? MaterialProcessor()
        => ReflectionVoxelStorage.Get(component,"MaterialProcessor")
            as Keen.VRage.Voxels.Definitions.VoxelMaterialDefinitionPostProcessor;

    // ── Engine deposit detection (DetectMaterialDeposits) ─────────────────────
    // Keen's own Task type (a struct): poll IsCompleted across scene ticks, never block.
    private bool depositsStarted;
    private bool depositsEmpty; // processor/materials unavailable: complete immediately with no deposits
    private Keen.VRage.Library.Threading.Task<
        Keen.VRage.Library.Collections.PooledList<Keen.VRage.Voxels.Components.VoxelMaterialDeposit>> depositTask;
    public bool DepositsStarted => depositsStarted;
    public bool DepositsComplete => depositsStarted && (depositsEmpty || depositTask.IsCompleted);
    public List<VoxelDeposit> DepositResult
    {
        get
        {
            var result = new List<VoxelDeposit>();
            if (!DepositsComplete) return result;
            try
            {
                if (!depositTask.TryGetResult(out var list) || list == null) return result;
                foreach (var deposit in list)
                {
                    try
                    {
                        var bounds = deposit.Bounds;
                        result.Add(new(deposit.Material.Name.ToString(),
                            [bounds.Min.X, bounds.Min.Y, bounds.Min.Z],
                            [bounds.Max.X, bounds.Max.Y, bounds.Max.Z],
                            Id, Frame.Origin.Array, Frame.X.Array, Frame.Y.Array, Frame.Z.Array));
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }
    }
    public void StartDepositDetection()
    {
        if (depositsStarted) return;
        depositsStarted = true;
        try
        {
            var processor = MaterialProcessor();
            if (processor == null) { depositsEmpty = true; return; }
            var definitions = DefinitionsFromProcessor(processor);
            if (definitions.Count == 0) { depositsEmpty = true; return; }
            var materials = ImmutableList.CreateRange(definitions);
            var center = Frame.World(new(Size.X / 2.0, Size.Y / 2.0, Size.Z / 2.0));
            var diagonal = Math.Sqrt(Size.X * Size.X + Size.Y * Size.Y + Size.Z * Size.Z);
            var voxel = Math.Sqrt(V3.Dot(Frame.X, Frame.X));
            var sphere = new BoundingSphereD(new Vector3D(center.X, center.Y, center.Z), 0.5 * diagonal * voxel + 2.0);
            depositTask = component.DetectMaterialDeposits(sphere, materials);
        }
        catch { depositsEmpty = true; }
    }
    private static List<VoxelMaterialDefinition> DefinitionsFromProcessor(
        Keen.VRage.Voxels.Definitions.VoxelMaterialDefinitionPostProcessor processor)
    {
        // All materials: stone clusters map asteroid obstacles, ore names mark mineable veins.
        // Clients filter by name; single-voxel stone deposits are the price of the obstacle map.
        var result=new List<VoxelMaterialDefinition>();
        var field=processor.GetType().GetField("_voxelMaterialByIndex",
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if(field?.GetValue(processor) is System.Collections.IDictionary map)
            foreach(var value in map.Values)
                if(value is VoxelMaterialDefinition definition)
                    result.Add(definition);
        return result;
    }
    private static Dictionary<byte,string> BuildLegend(Keen.VRage.Voxels.Definitions.VoxelMaterialDefinitionPostProcessor processor)
    {
        var result=new Dictionary<byte,string>();
        for(byte index=0;index<255;index++)
        {
            try
            {
                if(processor.TryGetMaterialByIndex(index,out var definition))
                    result[index]=definition.Name.ToString();
            }
            catch { break; }
        }
        return result;
    }
}
