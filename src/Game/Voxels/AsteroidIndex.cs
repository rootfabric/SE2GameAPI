using System.Collections;
using System.Reflection;
namespace SE2GameAPI.Game.Voxels;

// SE1-style asteroid index: the procedural generator computes sector content
// deterministically from the world seed. We request sectors around a position
// WITHOUT materializing entities and read the prepared spawn candidates
// (position + world AABB) before any entity exists in the scene.
internal static class AsteroidIndex
{
    private static object? generator;
    private static readonly BindingFlags M = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static object? Generator(object session)
    {
        if (generator != null) return generator;
        try
        {
            // session.SessionComponents is a DCS Entity; find the generator component on it.
            var componentsEntity = ReflectionVoxelStorage.Get(session, "SessionComponents");
            if (componentsEntity == null) { Diagnostics = "session_components_null"; return null; }
            var components = ReflectionVoxelStorage.Get(componentsEntity, "Components") as IEnumerable;
            if (components == null)
            {
                // ImmutableArray<T> boxes poorly through reflection; try IList via ICollection
                var raw = ReflectionVoxelStorage.Get(componentsEntity, "Components");
                if (raw is System.Collections.ICollection collection) components = collection.Cast<object>();
            }
            if (components == null) { Diagnostics = "components_enumerable_unavailable"; return null; }
            var names = new List<string>();
            foreach (var component in components)
            {
                names.Add(component.GetType().Name);
                if (component.GetType().Name == "ProceduralGeneratorSessionComponent")
                    return generator = component;
            }
            Diagnostics = "generator_not_found:" + string.Join(",", names.Take(64));
        }
        catch (Exception e) { Diagnostics = "generator_error:" + e.GetBaseException().Message; }
        return null;
    }

    internal static string? Diagnostics { get; private set; }
    // Generator captured via the SpaceProbe registry (server variant lives in VRage.Game).
    internal static object? CapturedGenerator => generator;

    // Fallback for worlds without an active procedural generator (scenario worlds):
    // every voxel body registered in the SpaceProbe is enumerable with its world AABB,
    // including offloaded ones — giving SE1-style asteroid coordinates.
    internal static List<VoxelAsteroid> ReadRegisteredBodies(object session)
    {
        var result = new List<VoxelAsteroid>();
        Diagnostics = null; // fresh diagnostics for this path
        object? probe = null;
        var componentsEntity = ReflectionVoxelStorage.Get(session, "SessionComponents");
        var components = componentsEntity == null ? null
            : ReflectionVoxelStorage.Get(componentsEntity, "Components") as IEnumerable;
        if (components == null && componentsEntity != null)
        {
            var raw = ReflectionVoxelStorage.Get(componentsEntity, "Components");
            if (raw is System.Collections.ICollection collection) components = collection.Cast<object>();
        }
        if (components != null)
            foreach (var component in components)
                if (component.GetType().Name == "SpaceProbeSessionComponent")
                    probe = component;
        if (probe == null) { Diagnostics = "probe_unavailable"; return result; }
        var providers = ReflectionVoxelStorage.Get(probe, "_preloadableProviders") as IDictionary;
        if (providers == null) { Diagnostics = "providers_unavailable"; return result; }
        foreach (var entry in providers.Values)
        {
            try
            {
                // Values are PreloadableInfo wrappers; unwrap to the ISpaceProbePreloadable field.
                var value = entry;
                if (entry.GetType().Name == "PreloadableInfo")
                {
                    object? unwrapped = null;
                    foreach (var field in entry.GetType().GetFields(M))
                    {
                        var candidate = field.GetValue(entry);
                        if (candidate != null && candidate.GetType().IsClass
                            && !candidate.GetType().IsPrimitive && candidate is not string
                            && candidate.GetType().Name != "Nullable`1")
                        { unwrapped = candidate; break; }
                    }
                    if (unwrapped == null) continue;
                    value = unwrapped;
                }
                var typeName = value.GetType().Name;
                // The generator itself is registered as a probe provider (Server variant
                // lives in VRage.Game, not VRage.Core.Game) — capture it and bail out;
                // the caller will switch to the seed-sector path.
                if (typeName.Contains("ProceduralGenerator"))
                {
                    generator = value;
                    Diagnostics = "generator_via_probe";
                    return result;
                }
                var baseName = value.GetType().BaseType?.Name ?? "";
                if (typeName != "VoxelStorageComponentBase" && !baseName.Contains("VoxelStorageComponent"))
                {
                    if (result.Count == 0 && Diagnostics == null)
                        Diagnostics = "provider_type:" + value.GetType().FullName;
                    continue;
                }
                var storage = ReflectionVoxelStorage.Get(value, "Storage");
                var sizeRaw = storage == null ? null : ReflectionVoxelStorage.Get(storage, "Size");
                if (sizeRaw == null) continue;
                double S(string axis) => Convert.ToDouble(sizeRaw.GetType().GetField(axis)!.GetValue(sizeRaw));
                var voxel = Convert.ToDouble(ReflectionVoxelStorage.Get(value, "VoxelSize") ?? 1.0);
                var compute = value.GetType().GetMethod("ComputeCenteredTransform", M);
                if (compute == null) continue;
                var transform = compute.Invoke(value, null);
                if (transform == null) continue;
                double P(string axis) => Convert.ToDouble(
                    transform.GetType().GetField("Position")!.GetValue(transform)!.GetType().GetField(axis)!.GetValue(
                        transform.GetType().GetField("Position")!.GetValue(transform)));
                var halfX = S("X") * voxel * .5; var halfY = S("Y") * voxel * .5; var halfZ = S("Z") * voxel * .5;
                var (cx, cy, cz) = (P("X"), P("Y"), P("Z"));
                result.Add(new(
                    [cx, cy, cz],
                    [cx - halfX, cy - halfY, cz - halfZ],
                    [cx + halfX, cy + halfY, cz + halfZ],
                    "VoxelBody", 0, 0, 0, 0));
                if (result.Count >= 512) break;
            }
            catch { }
        }
        if (result.Count == 0 && providers.Count > 0 && Diagnostics == null)
            Diagnostics = "providers_count:" + providers.Count;
        else if (result.Count == 0 && providers.Count == 0 && Diagnostics == null)
            Diagnostics = "providers_empty";
        if (result.Count > 0) Diagnostics = null;
        if (result.Count >= 512) { }
        Diagnostics = result.Count > 0 ? null : Diagnostics ?? "providers_empty";
        return result;
    }

    internal static long SectorKeyInt(object gen, double[] position, out int sx, out int sy, out int sz)
    {
        sx = sy = sz = 0;
        var keyMethod = gen.GetType().GetMethod("GetEntitySectorKey", M);
        if (keyMethod == null) return -1;
        var vecType = keyMethod.GetParameters()[0].ParameterType;
        var vec = Activator.CreateInstance(vecType,
            new[] { Convert.ChangeType(position[0], typeof(double)), Convert.ChangeType(position[1], typeof(double)), Convert.ChangeType(position[2], typeof(double)) });
        if (vec == null) return -1;
        var key = keyMethod.Invoke(gen, [vec]);
        if (key == null) return -1;
        sx = (int)(key.GetType().GetField("X")!.GetValue(key) ?? 0);
        sy = (int)(key.GetType().GetField("Y")!.GetValue(key) ?? 0);
        sz = (int)(key.GetType().GetField("Z")!.GetValue(key) ?? 0);
        return 0;
    }

    // Sector edge length in metres (from bounds of sector (0,0,0)).
    internal static double SectorSize(object gen)
    {
        var boundsMethod = gen.GetType().GetMethod("GetEntitySectorBounds", M);
        if (boundsMethod == null) return 0;
        var vecType = boundsMethod.GetParameters()[0].ParameterType;
        var zero = Activator.CreateInstance(vecType, new object[] { 0, 0, 0 });
        if (zero == null) return 0;
        var bounds = boundsMethod.Invoke(gen, [zero]);
        if (bounds == null) return 0;
        double Get(string axis, string part) =>
            Convert.ToDouble(bounds.GetType().GetField(part)?.GetValue(bounds) is object v
                ? v.GetType().GetField(axis)!.GetValue(v) : 0);
        var sizeX = Get("X", "Max") - Get("X", "Min");
        return sizeX > 0 ? sizeX : 0;
    }

    // Request a sector, wait for its deterministic generation, read prepared candidates.
    // Never materializes: entities appear only when a player actually approaches.
    internal static List<VoxelAsteroid> ReadSector(object gen, int sx, int sy, int sz)
    {
        var result = new List<VoxelAsteroid>();
        var requestMethod = gen.GetType().GetMethod("RequestEntitySector", M);
        if (requestMethod == null) return result;
        var vecType = requestMethod.GetParameters()[0].ParameterType;
        object? sector = null;
        try
        {
            var id = Activator.CreateInstance(vecType, new object[] { sx, sy, sz });
            if (id == null) return result;
            sector = requestMethod.Invoke(gen, [id]);
            if (sector == null) return result;
        }
        catch { return result; }
        // Generation is usually instant for a pure-math seed sector; wait bounded
        // (scene thread) and return whatever is prepared if the task lags.
        var depTask = sector.GetType().GetMethod("GetGenerationDependencyTask", M)?.Invoke(sector, null);
        int spins = 0;
        while (depTask != null && !(bool)(depTask.GetType().GetProperty("IsCompleted")?.GetValue(depTask) ?? false))
        {
            if (++spins > 20) break; // max 100 ms on the DCS thread
            System.Threading.Thread.Sleep(5);
        }
        var prepared = sector.GetType().GetField("_preparedEntities", M)?.GetValue(sector) as IEnumerable;
        if (prepared == null) return result;
        foreach (var candidate in prepared)
        {
            try
            {
                var candidateType = candidate.GetType();
                var entityId = candidateType.GetField("ID", M)?.GetValue(candidate);
                var sectorId = entityId?.GetType().GetProperty("SectorID")?.GetValue(entityId);
                var entityNumber = entityId?.GetType().GetProperty("EntityID")?.GetValue(entityId);
                var sizeObject = candidateType.GetField("SizeObject", M)?.GetValue(candidate);
                var worldAabb = sizeObject?.GetType().GetProperty("WorldAABB")?.GetValue(sizeObject);
                if (worldAabb == null) continue;
                double Min(string a) => Convert.ToDouble(
                    worldAabb.GetType().GetField("Min")!.GetValue(worldAabb)!.GetType().GetField(a)!.GetValue(
                        worldAabb.GetType().GetField("Min")!.GetValue(worldAabb)));
                double Max(string a) => Convert.ToDouble(
                    worldAabb.GetType().GetField("Max")!.GetValue(worldAabb)!.GetType().GetField(a)!.GetValue(
                        worldAabb.GetType().GetField("Max")!.GetValue(worldAabb)));
                var builder = candidateType.GetField("ObjectBuilder", M)?.GetValue(candidate);
                var type = builder?.GetType().Name ?? "Asteroid";
                int SecAxis(object sid, string a) => Convert.ToInt32(sid?.GetType().GetField(a)?.GetValue(sid) ?? 0);
                result.Add(new(
                    [(Min("X") + Max("X")) / 2, (Min("Y") + Max("Y")) / 2, (Min("Z") + Max("Z")) / 2],
                    [Min("X"), Min("Y"), Min("Z")], [Max("X"), Max("Y"), Max("Z")],
                    type, SecAxis(sectorId, "X"), SecAxis(sectorId, "Y"), SecAxis(sectorId, "Z"),
                    Convert.ToInt32(entityNumber ?? 0)));
                if (result.Count >= 128) break; // per-sector sanity cap
            }
            catch { }
        }
        // Release the sector reference; entities were never materialized.
        try { sector.GetType().GetMethod("Dispose", M)?.Invoke(sector, null); }
        catch { }
        return result;
    }
}
