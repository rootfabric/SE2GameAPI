using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
var dir = @"D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Game2.Simulation.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
static IEnumerable<TypeDefinition> All(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany<TypeDefinition, TypeDefinition>(All)) yield return n;
}
foreach (var name in args)
{
    var matches = asm.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All)
        .Where(t => t.Name == name || t.FullName == name).ToList();
    if (matches.Count == 0) { Console.WriteLine("NOT FOUND " + name); continue; }
    foreach (var t in matches)
    {
        Console.WriteLine("=== " + t.FullName + " : " + (t.BaseType?.FullName ?? ""));
        foreach (var fd in t.Fields) Console.WriteLine($"  field {fd.FieldType.Name} {fd.Name}");
        foreach (var p in t.Properties) Console.WriteLine($"  prop  {p.PropertyType.Name} {p.Name}");
        foreach (var m in t.Methods.Where(m => !m.IsConstructor && (m.IsPublic || m.IsFamily || m.IsAssembly)))
            Console.WriteLine($"  meth  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.Name + " " + pp.Name))})");
    }
}
