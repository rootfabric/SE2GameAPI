using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
var dir = @"D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Game2.Simulation.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
foreach (var spec in args)
{
    var parts = spec.Split(':');
    var typeName = parts[0]; var methodName = parts[1];
    var t = asm.MainModule.Types.FirstOrDefault(x => x.FullName == typeName);
    var methods = t?.Methods.Where(m => m.Name == methodName).ToList();
    if (methods == null || methods.Count == 0) { Console.WriteLine("NOT FOUND " + spec); continue; }
    foreach (var m in methods)
    {
        Console.WriteLine($"=== {t!.FullName}::{m.Name} ({m.Parameters.Count} args)");
        if (m.Body == null) { Console.WriteLine("  (no body)"); continue; }
        foreach (var i in m.Body.Instructions)
            Console.WriteLine($"  IL_{i.Offset:x4}: {i.OpCode} {i.Operand}");
    }
}
