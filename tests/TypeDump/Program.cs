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
var t = sim.MainModule.Types.SelectMany<TypeDefinition, TypeDefinition>(All).First(x => x.Name == "MissionSessionComponent");
var m = t.Methods.First(x => x.Name == "TryConvertBlockToProjection");
foreach (var ins in m.Body.Instructions)
{
    if (ins.Operand is MethodReference mr && mr.Name == "Get")
        Console.WriteLine($"{ins.OpCode.Name} {mr.DeclaringType.FullName}<{(mr as GenericInstanceMethod != null ? string.Join(",", ((GenericInstanceMethod)mr).GenericArguments.Select(a => a.FullName)) : "?")}>::{mr.Name}({string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name))})");
}
