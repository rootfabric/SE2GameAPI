using System.Collections;
using System.Reflection;
using Keen.VRage.Voxels.Components;
namespace SE2GameAPI.Game.Voxels;

internal static class ReflectionVoxelCatalog
{
    private static Type[]? voxelTypes;
    internal static VoxelCatalog Discover(object session)
    {
        var found=new List<IVoxelStorage>(); var issues=new List<string>();
        var scene=ReflectionVoxelStorage.Get(session,"Scene") ?? throw new NotSupportedException("scene_unavailable");
        if(voxelTypes==null)
        {
            var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="VRage.Voxels")
                ?? Assembly.Load("VRage.Voxels");
            // Prefer logical voxel entities; physics components alone may omit distant/unbuilt chunks.
            // Game2 build 4.0.0.3165 has no "VoxelComponent"; storages are exposed via concrete
            // Simple/Procedural/Dynamic components (see tests/VoxelApiProbe artifacts).
            voxelTypes=assembly.GetTypes().Where(t=>t.Namespace=="Keen.VRage.Voxels.Components" &&
                t.Name is "SimpleVoxelStorageComponent" or "ProceduralVoxelStorageComponent"
                    or "DynamicVoxelStorageComponent" or "VoxelStorageComponent").ToArray();
            if(voxelTypes.Length==0)
                voxelTypes=assembly.GetTypes().Where(t=>t.FullName=="Keen.VRage.Voxels.Components.VoxelPhysicsComponent").ToArray();
            if(voxelTypes.Length==0)
                throw new NotSupportedException("voxel_component_type_unavailable: run VoxelApiProbe against this Game2 build");
        }
        var query=session.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public).FirstOrDefault(m=>
            m.Name=="GetEntitiesOfType" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length==1 && m.GetParameters().Length==0)
            ?? FindQueryExtension(session.GetType())
            ?? throw new NotSupportedException("session_entity_query_unavailable");
        int visited=0;
        foreach(var type in voxelTypes)
        {
            if(type.Name=="VoxelPhysicsComponent") issues.Add("physics_components_only: unloaded terrain is unknown");
            object? result=query.MakeGenericMethod(type).Invoke(query.IsStatic?null:session,query.IsStatic?[session]:null);
            if(result is not IEnumerable entities) { issues.Add($"non_enumerable_query:{type.FullName}"); continue; }
            int matches=0;
            // The engine iterator is used only in this DCS call, never kept across frames.
            foreach(var entity in entities)
            {
                matches++;
                if(++visited>512) { issues.Add("catalogue_entity_limit"); return new(found.ToArray(),issues.ToArray()); }
                try
                {
                    var components=ReflectionVoxelStorage.Get(entity,"Components") as IEnumerable;
                    object? component=type.IsInstanceOfType(entity)?entity:components?.Cast<object>().FirstOrDefault(type.IsInstanceOfType);
                    if(component==null)
                    {
                        var get=entity.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public).FirstOrDefault(m=>
                            m.Name=="Get" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length==1 && m.GetParameters().Length==0);
                        component=get?.MakeGenericMethod(type).Invoke(entity,null);
                    }
                    if(component==null) { issues.Add($"component_access_unavailable:{type.FullName}"); continue; }
                    var id=ReflectionVoxelStorage.Get(entity,"DEntity") ?? ReflectionVoxelStorage.Get(component,"DEntity")
                        ?? throw new NotSupportedException("voxel_entity_id_unavailable");
                    // Session-scoped liveness is enough for the scan service: scans are short-lived
                    // and epoch-fenced; a storage read cannot outlive the session that planned it.
                    bool Alive()=>ReflectionVoxelStorage.Get(session,"IsUnloading") is not true;
                    // Prefer the typed adapter: StorageData is a ref struct and unreadable via reflection.
                    if(component is VoxelStorageComponentBase typed)
                    {
                        var bound=TypedVoxelStorage.TryBind(id.ToString()!,typed,Alive);
                        if(bound!=null)
                        {
                            if(found.Any(s=>ReferenceEquals(s.Identity,bound.Identity))) continue;
                            if(found.Count==VoxelScanService.MaxStorages) { issues.Add("catalogue_storage_limit"); return new(found.ToArray(),issues.ToArray()); }
                            found.Add(bound);
                        }
                        continue;
                    }
                    var storage=ReflectionVoxelStorage.Bind(id.ToString()!,component,Alive);
                    if(found.Any(s=>ReferenceEquals(s.Identity,storage.Identity))) continue;
                    if(found.Count==VoxelScanService.MaxStorages) { issues.Add("catalogue_storage_limit"); return new(found.ToArray(),issues.ToArray()); }
                    found.Add(storage);
                }
                catch(Exception e) { if(issues.Count<64) issues.Add(e.GetBaseException().Message); }
            }
            if(matches==0) issues.Add($"query_empty:{type.Name}");
        }
        return new(found.ToArray(),issues.Distinct().ToArray());
    }
    private static MethodInfo? FindQueryExtension(Type sessionType)
    {
        foreach(var a in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="VRage.Core.Game"))
        foreach(var t in a.GetTypes().Where(t=>t.IsAbstract && t.IsSealed && t.Name.Contains("Session",StringComparison.Ordinal)))
        foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.Static))
            if(m.Name=="GetEntitiesOfType" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length==1 &&
                m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(sessionType)) return m;
        return null;
    }

}
