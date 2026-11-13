using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace SE2GameAPI.Game.Voxels;

// Cooperative scene service. No worker ever dereferences storage or scene objects.
internal sealed class VoxelScanService(IVoxelTransport transport)
{
    internal const int TileEdge=16, MaxTiles=512, MaxStorages=64, MaxCacheTiles=512;
    internal const long CacheAgeMs=5000;
    private sealed record Plan(IVoxelStorage Storage, I3 Min, I3 Size, int Lod);
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
        // Deposit-detection mode (action "deposits"): engine DetectMaterialDeposits per storage.
        public bool DepositsMode;
        public List<IVoxelStorage> DepositTargets = [];
        public List<VoxelDeposit> Deposits = [];
        // Asteroid-index mode (action "asteroids"): deterministic seed sectors, no proximity.
        public bool AsteroidsMode;
        public List<(int X,int Y,int Z)> PendingSectors = [];
        public List<VoxelAsteroid> Asteroids = [];
        public object? Generator;
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
        s.MaterialLegend,s.DepositsMode?s.Deposits.ToArray():null,
        s.AsteroidsMode?s.Asteroids.ToArray():null);
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
            else if(r.Action=="asteroids" && !string.IsNullOrWhiteSpace(r.GridId) &&
                double.IsFinite(r.Radius) && r.Radius>=1 && r.Radius<=300 &&
                r.ExpiresAtMs>now && r.ExpiresAtMs<=now+300000)
                StartAsteroids(world,r,now);
            else if(r.Action=="deposits" && !string.IsNullOrWhiteSpace(r.GridId) &&
                double.IsFinite(r.Radius) && r.Radius>=1 && r.Radius<=128 &&
                r.ExpiresAtMs>now && r.ExpiresAtMs<=now+120000)
                StartDeposits(world,r,now);
            else if(r.Action=="scan" && r.Lod>=0 && r.Lod<=4 && !string.IsNullOrWhiteSpace(r.GridId) &&
                double.IsFinite(r.Radius) && r.Radius>=1 && r.Radius<=128*(1<<r.Lod) &&
                r.ExpiresAtMs>now && r.ExpiresAtMs<=now+120000)
                Start(world,r,now);
            else
                Reject(r,"invalid_request",now);
            Flush(); return; // Never combine catalogue discovery and a storage read in one turn.
        }
        if(active==null) return;
        var s=active;
        if(s.Request.ExpiresAtMs<=now)
        { outbound=new(Status(s,"partial","deadline",now)); active=null; Flush(); return; }
        if(s.AsteroidsMode)
        {
            // One sector per DCS turn: bounded reflection, no entity materialization.
            while(s.PendingSectors.Count>0 && hasTime())
            {
                var (sx,sy,sz)=s.PendingSectors[0];
                s.PendingSectors.RemoveAt(0);
                s.Reads++;
                s.Asteroids.AddRange(AsteroidIndex.ReadSector(s.Generator!,sx,sy,sz));
                if(s.Asteroids.Count>=2048) { s.Issues.Add("asteroid_limit"); break; }
            }
            if(s.PendingSectors.Count==0 || s.Issues.Contains("asteroid_limit"))
            {
                s.Asteroids.Sort((a,b)=>Dist2(a.Center,s.Pose.Position).CompareTo(Dist2(b.Center,s.Pose.Position)));
                outbound=new(Status(s,"complete",null,now));
                active=null; Flush(); return;
            }
            return;
        }
        if(s.DepositsMode)
        {
            // Poll engine deposit-detection tasks; finish once every storage reports.
            bool allDone=s.DepositTargets.Count>0;
            foreach(var target in s.DepositTargets)
            {
                if(!target.DepositsStarted) target.StartDepositDetection();
                if(!target.DepositsComplete) allDone=false;
            }
            if(allDone || s.Issues.Count>0 && allDone)
            {
                foreach(var target in s.DepositTargets) s.Deposits.AddRange(target.DepositResult);
                outbound=new(Status(s,"complete",null,now));
                active=null; Flush(); return;
            }
            return;
        }
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
                try { data=p.Storage.Read(p.Min,p.Size,p.Lod); material=p.Storage.ReadMaterials(p.Min,p.Size,p.Lod); }
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
    private static double Dist2(double[] a, double[] b) =>
        (a[0]-b[0])*(a[0]-b[0])+(a[1]-b[1])*(a[1]-b[1])+(a[2]-b[2])*(a[2]-b[2]);

    private void StartAsteroids(IVoxelWorld world,VoxelRequest r,long now)
    {
        try
        {
            var pose=world.FindGrid(r.GridId);
            if(pose==null) { Reject(r,"grid_not_found",now); return; }
            var session=world.SessionObject ?? throw new InvalidOperationException("session_unavailable");
            var generator=AsteroidIndex.Generator(session);
            if(generator==null)
            {
                // Scenario worlds: the generator may only be reachable through the
                // SpaceProbe provider registry — read it (this also captures the generator).
                var (bodies,captured)=AsteroidIndex.ReadRegisteredBodies(session);
                generator=captured;
                if(generator==null && (bodies.Count>0 || AsteroidIndex.Diagnostics is null or "providers_empty"))
                {
                    bodies.Sort((a,b)=>Dist2(a.Center,pose.Position).CompareTo(Dist2(b.Center,pose.Position)));
                    active=new(r,pose,new Queue<Plan>(),["asteroid_index_registered_bodies"])
                    {
                        AsteroidsMode=true,
                        PendingSectors=[],
                        Asteroids=bodies,
                    };
                    outbound=new(Status(active,"complete",null,now));
                    return;
                }
                if(generator==null)
                    throw new InvalidOperationException("generator_unavailable:"+AsteroidIndex.Diagnostics);
            }
            if(AsteroidIndex.SectorKeyInt(generator,pose.Position,out var cx,out var cy,out var cz)!=0)
                throw new InvalidOperationException("sector_key_unavailable");
            var sectorSize=AsteroidIndex.SectorSize(generator);
            if(sectorSize<=0) throw new InvalidOperationException("sector_bounds_unavailable");
            // sectors covering the requested sphere (radius in metres), capped for the DCS budget
            var sectorRange=Math.Max(1,(int)Math.Ceiling(r.Radius*1000.0/sectorSize));
            var range=Math.Min(sectorRange,8); // <= 17^3 = 4913 sectors, still one pass per turn
            var pending=new List<(int,int,int)>();
            for(int z=cz-range;z<=cz+range;z++)
            for(int y=cy-range;y<=cy+range;y++)
            for(int x=cx-range;x<=cx+range;x++)
                pending.Add((x,y,z));
            if(sectorRange>range) pending.Add((cx,cy,cz));
            active=new(r,pose,new Queue<Plan>(),["asteroid_index_from_seed"])
            {
                AsteroidsMode=true,
                PendingSectors=pending,
                Generator=generator,
            };
            outbound=new(Status(active,"scanning",null,now));
        }
        catch(Exception e) { Reject(r,e.GetBaseException().Message,now); }
    }

    private void StartDeposits(IVoxelWorld world,VoxelRequest r,long now)
    {
        try
        {
            var pose=world.FindGrid(r.GridId);
            if(pose==null) { Reject(r,"grid_not_found",now); return; }
            var catalog=world.Discover();
            var typed=catalog.Storages.Where(st=>st is { DepositsComplete: false } or { DepositsStarted: false }).ToList();
            var issues=catalog.Issues.ToList();
            if(typed.Count==0) issues.Add("no_typed_storage_for_deposits");
            active=new(r,pose,new Queue<Plan>(),issues.ToArray())
            {
                DepositsMode=true,
                DepositTargets=typed,
            };
            var legend=catalog.Storages.Select(st=>st.MaterialLegend).FirstOrDefault(l=>l!=null);
            if(legend!=null) active.MaterialLegend=legend.Select(kv=>kv.Key+"="+kv.Value).ToArray();
            outbound=new(Status(active,"scanning",null,now));
        }
        catch(Exception e) { Reject(r,e.GetBaseException().Message,now); }
    }

    private void Start(IVoxelWorld world,VoxelRequest r,long now)
    {
        try
        {
            var pose=world.FindGrid(r.GridId);
            if(pose==null && r.Center==null) { Reject(r,"grid_not_found",now); return; }
            // Optional explicit center: scan around an arbitrary point (e.g. an indexed
            // asteroid far from the anchor grid).
            V3 center;
            if(r.Center!=null)
            {
                if(r.Center.Length!=3 || r.Center.Any(v=>!double.IsFinite(v)))
                    throw new InvalidOperationException("invalid_center");
                center=new V3(r.Center[0],r.Center[1],r.Center[2]);
                pose ??= new VoxelPose(r.GridId,"asteroid",[center.X,center.Y,center.Z]);
            }
            else
            {
                if(pose==null) { Reject(r,"grid_not_found",now); return; }
                if(pose.Position.Length!=3 || pose.Position.Any(v=>!double.IsFinite(v)))
                    throw new InvalidOperationException("invalid_grid_position");
                center=new V3(pose.Position[0],pose.Position[1],pose.Position[2]);
            }
            var catalog=world.Discover();
            if(catalog.Storages.Length>MaxStorages) throw new InvalidOperationException("catalogue_limit");
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
                // Coarse-voxel mode: lod>0 samples every 2^lod-th voxel (LOD pyramid),
                // so the same 512-tile budget covers a much larger volume.
                var lod=Math.Clamp(r.Lod,0,6);
                var step=1<<lod;
                var lodSize=new I3(storage.Size.X/step,storage.Size.Y/step,storage.Size.Z/step);
                var lodLo=new I3(lo.X/step,lo.Y/step,lo.Z/step);
                var lodHi=new I3(Math.Min(hi.X/step,lodSize.X),Math.Min(hi.Y/step,lodSize.Y),Math.Min(hi.Z/step,lodSize.Z));
                if(lodLo.X>=lodHi.X || lodLo.Y>=lodHi.Y || lodLo.Z>=lodHi.Z) continue;
                // Align tiles to storage, not the moving grid, so overlapping requests reuse the cache.
                for(int z=lodLo.Z/TileEdge*TileEdge;z<lodHi.Z;z+=TileEdge)
                for(int y=lodLo.Y/TileEdge*TileEdge;y<lodHi.Y;y+=TileEdge)
                for(int x=lodLo.X/TileEdge*TileEdge;x<lodHi.X;x+=TileEdge)
                {
                    var size=new I3(Math.Min(TileEdge,lodSize.X-x),Math.Min(TileEdge,lodSize.Y-y),Math.Min(TileEdge,lodSize.Z-z));
                    plans.Add(new(storage,new(x,y,z),size,r.Lod));
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
