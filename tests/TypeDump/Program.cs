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
foreach (var dll in new[] { "VRage.Core.Game.dll", "Game2.Simulation.dll" })
{
    var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, dll), new ReaderParameters { AssemblyResolver = resolver });
    foreach (var name in new[] { "WorldTransform", "EntityTransformFunctions" })
    {
        var t = asm.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All).FirstOrDefault(x => x.Name == name);
        if (t != null)
        {
            Console.WriteLine($"{dll}: {t.FullName} (struct={t.IsValueType})");
            if (name == "WorldTransform")
                foreach (var c in t.Methods.Where(m => m.IsConstructor).Take(4))
                    Console.WriteLine($"  ctor({string.Join(",", c.Parameters.Select(p => p.ParameterType.Name))})");
        }
    }
}
