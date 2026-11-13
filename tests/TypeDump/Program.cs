using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
var dir = @"D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
var sim = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Game2.Simulation.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
var voxels = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "VRage.Voxels.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
var library = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "VRage.Library.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
static IEnumerable<TypeDefinition> All(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany<TypeDefinition, TypeDefinition>(All)) yield return n;
}
foreach (var name in args)
{
    foreach (var asm2 in new[] { voxels, sim, library })
    {
        var matches = asm2.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All)
            .Where(t => t.Name == name || t.FullName.EndsWith(name)).ToList();
        foreach (var t in matches)
        {
            Console.WriteLine("=== " + asm2.Name.Name + ": " + t.FullName);
            foreach (var p in t.Properties) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var fd in t.Fields) Console.WriteLine($"  field {fd.FieldType.Name} {fd.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsConstructor && (m.IsPublic || m.IsAssembly)))
                Console.WriteLine($"  meth {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType + " " + p.Name))})");
        }
    }
}
