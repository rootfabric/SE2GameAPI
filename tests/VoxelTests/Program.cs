using SE2GameAPI.Game.Voxels;
using System.Text.Json;

internal static class Program
{
    private static int passed;
    private static long now=1000000;
    private static void Check(bool ok,string name)
    {
        if(!ok) throw new Exception("FAIL: "+name);
        passed++; Console.WriteLine("PASS: "+name);
    }
    private static VoxelRequest Request(string id="g",double radius=3,bool fresh=false)=>new(Guid.NewGuid().ToString("N"),"epoch",id,now+90000,Radius:radius,Fresh:fresh);
    private static VoxelStatus Pump(VoxelScanService service,Transport transport,World world)
    {
        int start=transport.Output.Count;
        for(int i=0;i<1000;i++)
        {
            int reads=world.Storage.Reads;
            service.Run(world,"epoch",()=>true,now); now+=10;
            CheckCount(world.Storage.Reads-reads);
            if(transport.Output.Count>start && transport.Output[^1].Status.State is "complete" or "partial" or "rejected" or "cancelled")
                return transport.Output[^1].Status;
        }
        throw new Exception("scan did not terminate");
    }
    private static void CheckCount(int reads) { if(reads>1) throw new Exception("More than one storage read per turn"); }
    public static void Main()
    {
        var transport=new Transport(); var world=new World(); var service=new VoxelScanService(transport);
        transport.Input.Enqueue(Request());
        service.Run(world,"epoch",()=>false,now);
        Check(world.Storage.Reads==0 && transport.Input.Count==1,"no work without budget");
        service.Run(world,"epoch",()=>true,now);
        Check(world.Storage.Reads==0,"discovery and storage read are separate turns");
        var first=Pump(service,transport,world);
        Check(first.State=="complete" && first.PublishedTiles==8 && first.Reads==8,"all eight intersecting tiles, one read per turn");
        var tile=transport.Output.First(v=>v.Tile!=null).Tile!;
        Check(tile.Content.Length==4096 && tile.Size.SequenceEqual(new[]{16,16,16}),"dense complete 16 cubed content");
        Check(!first.NavigationSafe && first.Scope=="loaded_voxel_storages","coverage is not a navigation certificate");
        transport.Input.Enqueue(Request()); var second=Pump(service,transport,world);
        Check(second.Reads==0 && second.CacheHits==8,"overlapping scan reuses content cache");
        var repeated=transport.Output.Last(v=>v.Tile!=null).Tile!;
        Check(repeated.Cached && repeated.MeasuredAtMs<now,"cache retains acquisition timestamp");
        transport.Input.Enqueue(Request(fresh:true)); var fresh=Pump(service,transport,world);
        Check(fresh.Reads==8 && fresh.CacheHits==0,"fresh request bypasses cache");
        world.Storage.Version++;
        transport.Input.Enqueue(Request()); var changed=Pump(service,transport,world);
        Check(changed.Reads==8,"revision change invalidates cached cells");
        now+=6000; transport.Input.Enqueue(Request()); var expiredCache=Pump(service,transport,world);
        Check(expiredCache.Reads==8,"TTL invalidates cache even with unchanged revision");
        transport.Input.Enqueue(Request("missing")); Check(Pump(service,transport,world).Error=="grid_not_found","unknown grid rejected");
        transport.Input.Enqueue(Request(radius:double.NaN)); Check(Pump(service,transport,world).State=="rejected","NaN radius rejected");
        var stale=Request() with {Epoch="old"}; transport.Input.Enqueue(stale);
        Check(Pump(service,transport,world).Error=="stale_session","stale epoch rejected");
        world.Storage.Fail=true;
        transport.Input.Enqueue(Request(fresh:true)); var partial=Pump(service,transport,world);
        Check(partial.State=="partial" && partial.PublishedTiles==0 && partial.Issues.Length>0,"read failures remain unknown, not empty");
        world.Storage.Fail=false;
        world.Empty=true; transport.Input.Enqueue(Request()); var empty=Pump(service,transport,world);
        Check(empty.State=="partial" && empty.Issues.Length>0,"no storage is not proof of empty space"); world.Empty=false;
        transport.Block=true; int before=world.Storage.Reads;
        transport.Input.Enqueue(Request(fresh:true)); service.Run(world,"epoch",()=>true,now);
        for(int i=0;i<10;i++) service.Run(world,"epoch",()=>true,now);
        Check(world.Storage.Reads==before,"transport backpressure stops engine reads"); transport.Block=false;
        Pump(service,transport,world);
        var deadline=Request(fresh:true) with {ExpiresAtMs=now+10}; transport.Input.Enqueue(deadline);
        service.Run(world,"epoch",()=>true,now); now+=20;
        Check(Pump(service,transport,world).Error=="deadline","expired scan stops before another read");
        var cancel=Request(fresh:true); transport.Input.Enqueue(cancel); service.Run(world,"epoch",()=>true,now);
        transport.Input.Enqueue(cancel with {Action="cancel"});
        Check(Pump(service,transport,world).State=="cancelled","active scan cancellation");
        world.Storage.Huge=true; transport.Input.Enqueue(Request(radius:128));
        Check(Pump(service,transport,world).Error=="area_too_large_reduce_radius","bounded tile planning"); world.Storage.Huge=false;
        var frame=new VoxelFrame(new(1e9,-2e9,3e9),new(0,2,0),new(-2,0,0),new(0,0,2));
        var local=frame.Local(frame.World(new(7,9,11)));
        Check(Math.Abs(local.X-7)<1e-6 && Math.Abs(local.Y-9)<1e-6 && Math.Abs(local.Z-11)<1e-6,"double precision rotated world/storage transforms");
        var owner=new Owner();
        var binding=ReflectionVoxelStorage.Bind("fixture",owner,()=>true);
        byte[] data=binding.Read(new(0,0,0),new(16,16,16));
        Check(data.Length==4096 && data[17]==17 && data[4095]==255,"real reflection read path and x-fastest layout");
        Check(owner.Storage.Flags==StorageDataTypeFlags.Content && owner.Storage.Lod==0,"Content only and LOD0");
        Check(StorageData.Disposed,"temporary storage buffer disposed");
        bool failed=false;
        try { ReflectionVoxelStorage.Bind("bad",new MissingTransform(),()=>true); } catch(NotSupportedException) { failed=true; }
        Check(failed,"missing transform is unsupported, never guessed");
        var dead=ReflectionVoxelStorage.Bind("dead",owner,()=>false); failed=false;
        try { dead.Read(new(0,0,0),new(1,1,1)); } catch(InvalidOperationException) { failed=true; }
        Check(failed,"unloaded entity rejected before storage read");
        string json=JsonSerializer.Serialize(tile,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        Check(JsonDocument.Parse(json).RootElement.GetProperty("content").ValueKind==JsonValueKind.String,"byte payload uses JSON base64");
        Console.WriteLine($"{passed} checks passed; engine-independent tests, NOT a live SE2 verification.");
    }
}
internal sealed class Transport:IVoxelTransport
{
    public Queue<VoxelRequest> Input=new(); public List<VoxelEvent> Output=new(); public bool Block;
    public bool TryRead(out VoxelRequest? r)=>Input.TryDequeue(out r);
    public bool TryWrite(VoxelEvent e) { if(Block) return false; Output.Add(e); return true; }
}
internal sealed class World:IVoxelWorld
{
    public readonly Storage Storage=new(); public bool Empty;
    public VoxelPose? FindGrid(string id)=>id=="g"?new("g","Test grid",[0,0,0]):null;
    public VoxelCatalog Discover()=>new(Empty?[]:[Storage],[]);
}
internal sealed class Storage:IVoxelStorage
{
    public int Reads,Version; public bool Fail,Huge;
    public string Id=>"storage"; public object Identity=>this;
    public I3 Size=>Huge?new(1024,1024,1024):new(32,32,32);
    public VoxelFrame Frame=>new(Huge?new(-512,-512,-512):new(-16,-16,-16),new(1,0,0),new(0,1,0),new(0,0,1));
    public string? Revision=>Version.ToString();
    public byte[] Read(I3 min,I3 size) { Reads++; if(Fail) throw new InvalidOperationException("unloaded"); return Enumerable.Repeat((byte)255,size.Count).ToArray(); }
}
// These are contract fixtures, NOT redistributable game DLLs or claims about a particular game build.
internal enum StorageDataTypeFlags { Content=1,Material=2 }
internal enum VoxelRequestFlags { None=0 }
internal readonly record struct Vector3I(int X,int Y,int Z);
internal readonly record struct Vector3D(double X,double Y,double Z);
internal sealed class StorageData(StorageDataTypeFlags flags):IDisposable
{
    public static bool Disposed;
    public byte[] Buffer=[]; private Vector3I size;
    public void Resize(Vector3I min,Vector3I max) { size=new(max.X-min.X+1,max.Y-min.Y+1,max.Z-min.Z+1); Buffer=new byte[size.X*size.Y*size.Z]; Disposed=false; }
    public byte Content(ref Vector3I p)=>Buffer[p.X+size.X*(p.Y+size.Y*p.Z)];
    public void Dispose()=>Disposed=true;
}
internal sealed class Reader
{
    public Vector3I Size=>new(64,64,64); public StorageDataTypeFlags Flags; public int Lod;
    public void ReadRange(ref StorageData data,StorageDataTypeFlags flags,int lod,Vector3I min,Vector3I max,ref VoxelRequestFlags request)
    { Flags=flags; Lod=lod; for(int i=0;i<data.Buffer.Length;i++) data.Buffer[i]=(byte)i; }
}
internal sealed class Owner
{
    public Reader Storage { get; }=new();
    public Vector3D StorageToWorld(Vector3D v)=>new(100-v.Y,v.X,200+v.Z);
}
internal sealed class MissingTransform { public Reader Storage { get; }=new(); }
