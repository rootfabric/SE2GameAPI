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
foreach (var dll in new[] { "VRage.Core.Game.dll", "VRage.Core.dll", "VRage.DCS.dll" })
{
    var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, dll), new ReaderParameters { AssemblyResolver = resolver });
    foreach (var t in asm.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All)
             .Where(t => t.IsEnum && (t.Name.Contains("Spawn") || t.FullName.Contains("SpawnMode"))))
    {
        Console.WriteLine($"=== {dll}: {t.FullName}");
        foreach (var f in t.Fields.Where(f => f.IsStatic))
            Console.WriteLine($"  {f.Name} = {f.Constant}");
    }
}
