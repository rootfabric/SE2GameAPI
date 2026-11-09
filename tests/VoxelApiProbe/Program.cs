using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
if(args.Length<1) throw new ArgumentException("Usage: dotnet run --project tests/VoxelApiProbe -- <Game2-directory> [output.json]");
string game=Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving+=(_,name)=>
{
    string path=Path.Combine(game,name.Name+".dll");
    return File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VRage.Voxels.dll"));
Type[] types;
try { types=assembly.GetTypes(); }
catch(ReflectionTypeLoadException e) { types=e.Types.Where(t=>t!=null).Cast<Type>().ToArray(); Console.Error.WriteLine(string.Join("\n",e.LoaderExceptions.Select(x=>x?.Message))); }
var report=new
{
    Assembly=assembly.FullName,
    Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    Note="Metadata only. No constructors, getters or game code executed. Keep game DLLs private.",
    Types=types.Where(t=>t.Namespace?.Contains("Voxels.Components")==true || t.Name.Contains("Storage") || t.Name=="VoxelRequestFlags")
        .OrderBy(t=>t.FullName).Select(t=>new
        {
            t.FullName,t.IsValueType,t.IsByRefLike,
            Constructors=t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Select(c=>c.ToString()).ToArray(),
            Members=t.GetMembers(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
                .Where(m=>m.MemberType is MemberTypes.Field or MemberTypes.Property or MemberTypes.Method)
                .Select(m=>m.ToString()).ToArray()
        }).ToArray()
};
string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
if(args.Length>1) { var path=Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,json); }
else Console.WriteLine(json);
