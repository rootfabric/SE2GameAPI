using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
var dir = @"D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
static IEnumerable<TypeDefinition> All(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany<TypeDefinition, TypeDefinition>(All)) yield return n;
}
var sim = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Game2.Simulation.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
var t = sim.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All)
    .First(x => x.Name == "SeatComponent");
Console.WriteLine($"=== {t.FullName}");
foreach (var fd in t.Fields.Where(f => !f.IsStatic)) Console.WriteLine($"  field {fd.FieldType.Name} {fd.Name}");
foreach (var p in t.Properties) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
foreach (var m in t.Methods.Where(m => !m.IsConstructor && !m.IsStatic))
    Console.WriteLine($"  meth {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.Name))})");
// also ISeat
var iseat = sim.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All).FirstOrDefault(x => x.Name == "ISeat");
if (iseat != null)
{
    Console.WriteLine($"=== {iseat.FullName}");
    foreach (var m in iseat.Methods) Console.WriteLine($"  meth {m.ReturnType.Name} {m.Name}");
    foreach (var p in iseat.Properties) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
}
