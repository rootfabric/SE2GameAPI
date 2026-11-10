namespace SE2GameAPI.Game.Voxels;

// Wire types contain values only. No engine objects are sent to the Redis worker.
public sealed record VoxelRequest(string RequestId, string Epoch, string GridId,
    long ExpiresAtMs, string Action = "scan", double Radius = 32, bool Fresh = false);
public sealed record VoxelPose(string GridId, string GridName, double[] Position);
public sealed record VoxelTile(int Index, string StorageId, int[] Min, int[] Size,
    double[] Origin, double[] AxisX, double[] AxisY, double[] AxisZ,
    byte[] Content, long MeasuredAtMs, bool Cached, string? StorageRevision,
    byte[]? Material = null);
public sealed record VoxelStatus(string RequestId, string Epoch, string GridId, string State,
    string? Error, VoxelPose? Grid, double Radius, int PlannedTiles, int PublishedTiles,
    int Reads, int CacheHits, double MaxReadMs, string[] Issues, long UpdatedAtMs,
    string[]? MaterialLegend = null)
{
    public int ProtocolVersion => 1;
    public string Scope => "loaded_voxel_storages";
    public string ContentEncoding => "uint8_raw_x_fastest";
    // LOD0 samples are not a collision mesh or a clearance certificate.
    public bool NavigationSafe => false;
}
public sealed record VoxelEvent(VoxelStatus Status, VoxelTile? Tile = null);

internal readonly record struct V3(double X, double Y, double Z)
{
    public double[] Array => [X, Y, Z];
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public static V3 operator +(V3 a, V3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static V3 operator *(V3 a, double b) => new(a.X*b,a.Y*b,a.Z*b);
    public static double Dot(V3 a, V3 b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
}
internal readonly record struct I3(int X, int Y, int Z)
{
    public int[] Array => [X,Y,Z];
    public int Count => checked(X*Y*Z);
}
internal sealed record VoxelFrame(V3 Origin, V3 X, V3 Y, V3 Z)
{
    public double Determinant => V3.Dot(X,V3.Cross(Y,Z));
    public bool Valid => Origin.Finite && X.Finite && Y.Finite && Z.Finite && double.IsFinite(Determinant) && Math.Abs(Determinant)>1e-12;
    public V3 World(V3 p) => Origin+X*p.X+Y*p.Y+Z*p.Z;
    public V3 Local(V3 p)
    {
        var d=p-Origin; double det=Determinant;
        return new(V3.Dot(d,V3.Cross(Y,Z))/det,V3.Dot(d,V3.Cross(Z,X))/det,V3.Dot(d,V3.Cross(X,Y))/det);
    }
}
internal sealed record VoxelCatalog(IVoxelStorage[] Storages, string[] Issues);
internal interface IVoxelStorage
{
    string Id { get; }
    object Identity { get; }
    I3 Size { get; }
    VoxelFrame Frame { get; }
    string? Revision { get; }
    // Must be called on the scene/DCS thread. Returns a new owned array, LOD0, x fastest.
    byte[] Read(I3 min, I3 size);
    // Optional material channel (byte index per voxel); null when the storage cannot provide it.
    byte[]? ReadMaterials(I3 min, I3 size) => null;
    // Optional legend: material index -> readable name ("12=IronOre").
    IReadOnlyDictionary<byte, string>? MaterialLegend => null;
}
internal interface IVoxelWorld
{
    VoxelPose? FindGrid(string id);
    VoxelCatalog Discover();
}
internal interface IVoxelTransport
{
    bool TryRead(out VoxelRequest? request);
    bool TryWrite(VoxelEvent value);
}
