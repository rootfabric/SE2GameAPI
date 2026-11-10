using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace SE2GameAPI.Game.Voxels;

// Cooperative scene service. No worker ever dereferences storage or scene objects.
internal sealed class VoxelScanService(IVoxelTransport transport)
{
    internal const int TileEdge=16, MaxTiles=512, MaxStorages=64, MaxCacheTiles=512;
    internal const long CacheAgeMs=5000;
    private sealed record Plan(IVoxelStorage Storage, I3 Min, I3 Size);
    private sealed record IdentityStamp(long Value);
    private ConditionalWeakTable<object,IdentityStamp> identities=new();
    private long nextIdentity;
    private sealed record Key(long Identity, VoxelFrame Frame, I3 Min, I3 Size, string? Revision);
    private sealed record Entry(byte[] Data,byte[]? Material,long MeasuredAtMs);
    private sealed class Scan(VoxelRequest request,VoxelPose pose,Queue<Plan> plans,string[] issues)
    {
        public readonly VoxelRequest Request=request;
        public readonly VoxelPose Pose=pose;
        public readonly Queue<Plan> Plans=plans;
        public readonly List<string> Issues=issues.ToList();
        public readonly int Total=plans.Count;
        public int Published,Reads,Hits;
        public double MaxReadMs;
        public string[]? MaterialLegend;
    }
    private readonly Dictionary<Key,Entry> cache=new();
    private Scan? active;
    private VoxelEvent? outbound;
    private long resumeAt;
    private string epoch="";
    public void Reset(string value)
    {
        // Transport cancels old-epoch requests. Do not publish old scene data in a new epoch.
        epoch=value; active=null; outbound=null; cache.Clear(); identities=new(); nextIdentity=0; resumeAt=0;
    }
    private VoxelStatus Status(Scan s,string state,string? error,long now) => new(
        s.Request.RequestId,epoch,s.Request.GridId,state,error,s.Pose,s.Request.Radius,
        s.Total,s.Published,s.Reads,s.Hits,s.MaxReadMs,s.Issues.Distinct().Take(64).ToArray(),now,
        s.MaterialLegend);
    private void Reject(VoxelRequest r,string error,long now) => outbound=new(new(
        r.RequestId,r.Epoch,r.GridId,"rejected",error,null,double.IsFinite(r.Radius)?r.Radius:0,0,0,0,0,0,[],now));
    private bool Flush()
    {
        if(outbound==null) return true;
        if(!transport.TryWrite(outbound)) return false;
        outbound=null; return true;
    }
    public void Run(IVoxelWorld world,string currentEpoch,Func<bool> hasTime,long now)
    {
        if(epoch!=currentEpoch) Reset(currentEpoch);
        if(!Flush() || !hasTime()) return;
        // Admission/cancellation first, even while a slow read is cooling down.
        if(transport.TryRead(out var r) && r!=null)
        {
            if(r.Epoch!=epoch) Reject(r,"stale_session",now);
            else if(r.Action=="cancel")
            {
                if(active?.Request.RequestId==r.RequestId)
                { outbound=new(Status(active,"cancelled","requested",now)); active=null; }
                else outbound=new(new(r.RequestId,r.Epoch,r.GridId,"cancelled","requested",null,
                    double.IsFinite(r.Radius)?r.Radius:0,0,0,0,0,0,[],now));
            }
            else if(active!=null) Reject(r,"scanner_busy",now);
            else if(r.Action!="scan" || string.IsNullOrWhiteSpace(r.GridId) ||
                !double.IsFinite(r.Radius) || r.Radius<1 || r.Radius>128 ||
                r.ExpiresAtMs<=now || r.ExpiresAtMs>now+120000)
                Reject(r,"invalid_request",now);
            else Start(world,r,now);
            Flush(); return; // Never combine catalogue discovery and a storage read in one turn.
        }
        if(active==null) return;
        var s=active;
        if(s.Request.ExpiresAtMs<=now)
        { outbound=new(Status(s,"partial","deadline",now)); active=null; Flush(); return; }
        if(now<resumeAt || !hasTime()) return;
        if(s.Plans.Count==0)
        {
            outbound=new(Status(s,s.Issues.Count==0?"complete":"partial",
                s.Issues.Count==0?null:"incomplete_coverage",now));
            active=null; Flush(); return;
        }
        // One small tile maximum per turn, shared global budget with commands/navigation.
        var p=s.Plans.Dequeue();
        try
        {
            string? revision=p.Storage.Revision;
            var key=new Key(identities.GetValue(p.Storage.Identity,_=>new(++nextIdentity)).Value,p.Storage.Frame,p.Min,p.Size,revision);
            byte[] data; byte[]? material; long measured; bool cached=false;
            if(!s.Request.Fresh && cache.TryGetValue(key,out var e) && now>=e.MeasuredAtMs && now-e.MeasuredAtMs<=CacheAgeMs)
            { data=e.Data; material=e.Material; measured=e.MeasuredAtMs; cached=true; s.Hits++; }
            else
            {
                var timer=Stopwatch.StartNew();
                try { data=p.Storage.Read(p.Min,p.Size); material=p.Storage.ReadMaterials(p.Min,p.Size); }
                finally
                {
                    timer.Stop(); s.Reads++; s.MaxReadMs=Math.Max(s.MaxReadMs,timer.Elapsed.TotalMilliseconds);
                    if(timer.Elapsed.TotalMilliseconds>2) resumeAt=now+1000;
                }
                measured=now; // Start of measurement; cache hits never acquire a fresh timestamp.
                if(data.Length!=p.Size.Count) throw new InvalidOperationException("invalid_content_length");
                if(revision!=p.Storage.Revision) throw new InvalidOperationException("storage_changed_during_read");
                if(cache.Count>=MaxCacheTiles) cache.Remove(cache.MinBy(v=>v.Value.MeasuredAtMs).Key);
                cache[key]=new(data,material,measured);
            }
            var f=p.Storage.Frame;
            var origin=f.World(new(p.Min.X,p.Min.Y,p.Min.Z));
            var tile=new VoxelTile(s.Published,p.Storage.Id,p.Min.Array,p.Size.Array,
                origin.Array,f.X.Array,f.Y.Array,f.Z.Array,data,measured,cached,revision,material);
            s.Published++;
            outbound=new(Status(s,"scanning",null,now),tile);
        }
        catch(Exception e)
        {
            if(s.Issues.Count<64) s.Issues.Add($"{p.Storage.Id}@{p.Min}: {e.GetBaseException().Message}");
            outbound=new(Status(s,"scanning","tile_unavailable",now));
        }
        Flush();
    }
    private void Start(IVoxelWorld world,VoxelRequest r,long now)
    {
        try
        {
            var pose=world.FindGrid(r.GridId);
            if(pose==null) { Reject(r,"grid_not_found",now); return; }
            if(pose.Position.Length!=3 || pose.Position.Any(v=>!double.IsFinite(v)))
                throw new InvalidOperationException("invalid_grid_position");
            var catalog=world.Discover();
            if(catalog.Storages.Length>MaxStorages) throw new InvalidOperationException("catalogue_limit");
            var center=new V3(pose.Position[0],pose.Position[1],pose.Position[2]);
            var plans=new List<Plan>();
            foreach(var storage in catalog.Storages)
            {
                if(!storage.Frame.Valid || storage.Size.X<=0 || storage.Size.Y<=0 || storage.Size.Z<=0)
                    throw new InvalidOperationException("invalid_storage_geometry");
                var points=new List<V3>();
                for(int z=-1;z<=1;z+=2) for(int y=-1;y<=1;y+=2) for(int x=-1;x<=1;x+=2)
                    points.Add(storage.Frame.Local(center+new V3(x*r.Radius,y*r.Radius,z*r.Radius)));
                // Clamp in floating point before integer conversion (large world coordinates).
                int Lower(double v,int size)=>(int)Math.Clamp(Math.Floor(v),0,size);
                int Upper(double v,int size)=>(int)Math.Clamp(Math.Ceiling(v)+1,0,size);
                var lo=new I3(Lower(points.Min(p=>p.X),storage.Size.X),Lower(points.Min(p=>p.Y),storage.Size.Y),Lower(points.Min(p=>p.Z),storage.Size.Z));
                var hi=new I3(Upper(points.Max(p=>p.X),storage.Size.X),Upper(points.Max(p=>p.Y),storage.Size.Y),Upper(points.Max(p=>p.Z),storage.Size.Z));
                if(lo.X>=hi.X || lo.Y>=hi.Y || lo.Z>=hi.Z) continue;
                // Align tiles to storage, not the moving grid, so overlapping requests reuse the cache.
                for(int z=lo.Z/TileEdge*TileEdge;z<hi.Z;z+=TileEdge)
                for(int y=lo.Y/TileEdge*TileEdge;y<hi.Y;y+=TileEdge)
                for(int x=lo.X/TileEdge*TileEdge;x<hi.X;x+=TileEdge)
                {
                    var size=new I3(Math.Min(TileEdge,storage.Size.X-x),Math.Min(TileEdge,storage.Size.Y-y),Math.Min(TileEdge,storage.Size.Z-z));
                    plans.Add(new(storage,new(x,y,z),size));
                    if(plans.Count>MaxTiles) throw new InvalidOperationException("area_too_large_reduce_radius");
                }
            }
            plans.Sort((a,b)=>Distance(a,center).CompareTo(Distance(b,center)));
            var issues=catalog.Issues.ToList();
            if(catalog.Storages.Length==0) issues.Add("no_readable_storage: not proof of empty space");
            active=new(r,pose,new(plans),issues.ToArray());
            var legend=catalog.Storages.Select(s=>s.MaterialLegend).FirstOrDefault(l=>l!=null);
            if(legend!=null) active.MaterialLegend=legend.Select(kv=>kv.Key+"="+kv.Value).ToArray();
            outbound=new(Status(active,"scanning",null,now));
        }
        catch(Exception e) { Reject(r,e.GetBaseException().Message,now); }
    }
    private static double Distance(Plan p,V3 center)
    {
        var d=p.Storage.Frame.World(new(p.Min.X+p.Size.X*.5,p.Min.Y+p.Size.Y*.5,p.Min.Z+p.Size.Z*.5))-center;
        return V3.Dot(d,d);
    }
}
