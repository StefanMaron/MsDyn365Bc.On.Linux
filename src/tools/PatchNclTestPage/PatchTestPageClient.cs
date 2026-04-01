using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Patches TestPageClient.dll: changes CommunicationBroker.DefaultChannelOptions.Async
/// from true to false in TestPageClientSession.Create().
///
/// With Async=true, the test page client needs a dispatcher message pump to process
/// server callbacks. On Linux the TestDispatcher doesn't implement a pump, causing
/// deadlocks. With Async=false, calls are synchronous and no pump is needed.
/// </summary>
static class PatchTestPageClient
{
    public static int Run(string inputPath, string outputPath)
    {
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"ERROR: {inputPath} not found");
            return 1;
        }

        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath) ?? ".");

            var readerParams = new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadWrite = inputPath == outputPath
            };
            using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams);
            var module = assembly.MainModule;

            // Find TestPageClientSession.Create
            var type = module.GetTypes().FirstOrDefault(t => t.Name == "TestPageClientSession");
            if (type == null)
            {
                Console.WriteLine("ERROR: TestPageClientSession not found");
                return 1;
            }

            var method = type.Methods.FirstOrDefault(m => m.Name == "Create" && m.IsStatic);
            if (method == null)
            {
                Console.WriteLine("ERROR: Create method not found");
                return 1;
            }

            Console.WriteLine($"Found {type.FullName}.{method.Name}");

            // Find: ldc.i4.1 followed by callvirt set_Async(bool)
            bool patched = false;
            var instructions = method.Body.Instructions;
            for (int i = 0; i < instructions.Count - 1; i++)
            {
                if (instructions[i].OpCode == OpCodes.Ldc_I4_1 &&
                    instructions[i + 1].OpCode == OpCodes.Callvirt &&
                    instructions[i + 1].Operand is MethodReference mr &&
                    mr.Name == "set_Async")
                {
                    Console.WriteLine($"  Patching IL_{instructions[i].Offset:X4}: ldc.i4.1 → ldc.i4.0 (Async = false)");
                    instructions[i].OpCode = OpCodes.Ldc_I4_0;
                    patched = true;
                    break;
                }
            }

            if (!patched)
            {
                Console.WriteLine("ERROR: Could not find Async = true pattern");
                return 1;
            }

            if (inputPath == outputPath)
                assembly.Write();
            else
                assembly.Write(outputPath);
            Console.WriteLine($"Patched: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }
}
