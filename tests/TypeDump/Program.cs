using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
var dir = @"D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
var voxels = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "VRage.Voxels.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
static IEnumerable<TypeDefinition> All(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany<TypeDefinition, TypeDefinition>(All)) yield return n;
}
var t = voxels.MainModule.GetType("Keen.VRage.Voxels.Storage.IStorage");
if (t == null) { Console.WriteLine("not found"); return; }
Console.WriteLine("=== " + t.FullName);
foreach (var m in t.Methods.Where(m => !m.IsConstructor))
    Console.WriteLine($"  meth {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.Name + " " + pp.Name))})");
foreach (var p in t.Properties) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
