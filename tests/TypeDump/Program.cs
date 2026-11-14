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
foreach (var dll in new[] { "VRage.Core.dll", "VRage.Library.dll", "VRage.Core.Game.dll" })
{
    var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, dll), new ReaderParameters { AssemblyResolver = resolver });
    var t1 = asm.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All).FirstOrDefault(x => x.Name == "RelativeTransform");
    if (t1 == null) continue;
    Console.WriteLine($"=== {dll}: {t1.FullName} (struct={t1.IsValueType})");
    foreach (var fd in t1.Fields.Where(f => !f.IsStatic)) Console.WriteLine($"  field {fd.FieldType.Name} {fd.Name}");
}
