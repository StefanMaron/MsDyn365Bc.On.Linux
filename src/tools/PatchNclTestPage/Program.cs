using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Patches Nav.Ncl.dll to fix TestPage support on Linux.
///
/// Problem: NavTestPageBase.CreateTestClientSession() uses Assembly.Load()
/// with a version-qualified name to load TestPageClient.dll. This fails on
/// Linux because the assembly runs in a tenant ALC where version matching
/// doesn't resolve the DLL from the service directory.
///
/// Fix: Replace Assembly.Load(qualifiedName) with Assembly.LoadFrom(filePath)
/// where filePath is derived from the executing assembly's directory.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: PatchNclTestPage <command> <input.dll> [output.dll]");
            Console.WriteLine("Commands: ncl     - Patch Nav.Ncl.dll (Assembly.Load → LoadFrom)");
            Console.WriteLine("          client  - Patch TestPageClient.dll (Async=true → false)");
            return 1;
        }

        // Support both old (just file path) and new (command + path) syntax
        string command, inputPath, outputPath;
        if (args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            command = "ncl";
            inputPath = args[0];
            outputPath = args.Length > 1 ? args[1] : inputPath;
        }
        else
        {
            command = args[0];
            inputPath = args.Length > 1 ? args[1] : "";
            outputPath = args.Length > 2 ? args[2] : inputPath;
        }

        if (command == "client")
            return PatchTestPageClient.Run(inputPath, outputPath);
        if (command == "types")
            return PatchNavTypes.Run(inputPath, outputPath);

        // Default: patch Nav.Ncl.dll

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"ERROR: {inputPath} not found");
            return 1;
        }

        try
        {
            // Set up Cecil assembly resolver to find BC dependencies
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath) ?? ".");
            // Also search common BC service tier paths
            if (Directory.Exists("/bc/service"))
                resolver.AddSearchDirectory("/bc/service");

            var readerParams = new ReaderParameters
            {
                ReadWrite = inputPath == outputPath,
                AssemblyResolver = resolver
            };
            using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams);
            var module = assembly.MainModule;

            // Find CreateTestClientSession in any type
            TypeDefinition? type = null;
            MethodDefinition? method = null;
            foreach (var t in module.GetTypes())
            {
                var m = t.Methods.FirstOrDefault(m => m.Name == "CreateTestClientSession");
                if (m != null)
                {
                    type = t;
                    method = m;
                    break;
                }
            }
            if (type == null || method == null)
            {
                Console.WriteLine("ERROR: CreateTestClientSession not found in any type");
                return 1;
            }

            Console.WriteLine($"Found {type.FullName}.{method.Name}");
            Console.WriteLine($"  IL instructions: {method.Body.Instructions.Count}");

            // Strategy: Find the Assembly.Load(string) call and replace the preceding
            // string construction with a simple path, and the Load call with LoadFrom.
            //
            // Original IL pattern:
            //   call Assembly.GetExecutingAssembly()
            //   callvirt AssemblyName.get_FullName()
            //   ... string manipulation to build qualified name ...
            //   call Assembly.Load(string)
            //
            // Replacement:
            //   call Assembly.GetExecutingAssembly()
            //   callvirt Assembly.get_Location()
            //   call Path.GetDirectoryName(string)
            //   ldstr "Microsoft.Dynamics.Nav.Client.TestPageClient.dll"
            //   call Path.Combine(string, string)
            //   call Assembly.LoadFrom(string)

            var il = method.Body.GetILProcessor();
            var instructions = method.Body.Instructions;

            // Find Assembly.Load(string) call
            Instruction? loadCall = null;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                    && mr.Name == "Load" && mr.DeclaringType.Name == "Assembly"
                    && mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.Name == "String")
                {
                    loadCall = instr;
                    Console.WriteLine($"  Found Assembly.Load at IL_{instr.Offset:X4}");
                    break;
                }
            }

            if (loadCall == null)
            {
                Console.WriteLine("ERROR: Assembly.Load(string) call not found in method");
                return 1;
            }

            // Find Assembly.GetExecutingAssembly() call (start of the string building sequence)
            Instruction? getExecAsm = null;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                    && mr.Name == "GetExecutingAssembly" && mr.DeclaringType.Name == "Assembly")
                {
                    getExecAsm = instr;
                    Console.WriteLine($"  Found GetExecutingAssembly at IL_{instr.Offset:X4}");
                    break;
                }
            }

            if (getExecAsm == null)
            {
                Console.WriteLine("ERROR: GetExecutingAssembly() call not found");
                return 1;
            }

            // Import required method references
            var assemblyType = module.ImportReference(typeof(System.Reflection.Assembly));
            var loadFromMethod = module.ImportReference(
                typeof(System.Reflection.Assembly).GetMethod("LoadFrom", new[] { typeof(string) }));
            var getLocationMethod = module.ImportReference(
                typeof(System.Reflection.Assembly).GetProperty("Location")!.GetGetMethod()!);
            var getExecAsmMethod = module.ImportReference(
                typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly")!);
            var pathCombineMethod = module.ImportReference(
                typeof(System.IO.Path).GetMethod("Combine", new[] { typeof(string), typeof(string) }));
            var pathGetDirMethod = module.ImportReference(
                typeof(System.IO.Path).GetMethod("GetDirectoryName", new[] { typeof(string) }));

            // Remove all instructions from GetExecutingAssembly to Assembly.Load (inclusive),
            // replace with our sequence
            int startIdx = instructions.IndexOf(getExecAsm);
            int endIdx = instructions.IndexOf(loadCall);

            Console.WriteLine($"  Replacing IL_{getExecAsm.Offset:X4}..IL_{loadCall.Offset:X4} ({endIdx - startIdx + 1} instructions)");

            // Collect instructions to remove
            var toRemove = new System.Collections.Generic.List<Instruction>();
            for (int i = startIdx; i <= endIdx; i++)
                toRemove.Add(instructions[i]);

            // Insert new instructions before the first one to remove
            var insertBefore = (endIdx + 1 < instructions.Count) ? instructions[endIdx + 1] : null;

            // Remove old instructions
            foreach (var instr in toRemove)
                il.Remove(instr);

            // Insert new sequence:
            // 1. call Assembly.GetExecutingAssembly()
            // 2. callvirt get_Location()
            // 3. call Path.GetDirectoryName(string)
            // 4. ldstr "Microsoft.Dynamics.Nav.Client.TestPageClient.dll"
            // 5. call Path.Combine(string, string)
            // 6. call Assembly.LoadFrom(string)
            var newInstructions = new[]
            {
                il.Create(OpCodes.Call, getExecAsmMethod),
                il.Create(OpCodes.Callvirt, getLocationMethod),
                il.Create(OpCodes.Call, pathGetDirMethod),
                il.Create(OpCodes.Ldstr, "Microsoft.Dynamics.Nav.Client.TestPageClient.dll"),
                il.Create(OpCodes.Call, pathCombineMethod),
                il.Create(OpCodes.Call, loadFromMethod),
            };

            // Insert new instructions in correct order before the target
            // InsertBefore always inserts before the same target, so inserting
            // A,B,C,D,E,F before T gives: A,B,C,D,E,F,T (each pushes back)
            // We need to insert in REVERSE so the final order is correct:
            // Insert F before T → F,T
            // Insert E before F → E,F,T
            // Insert D before E → D,E,F,T etc.
            if (insertBefore != null)
            {
                // Insert each at the correct position by tracking the anchor
                var anchor = insertBefore;
                for (int i = newInstructions.Length - 1; i >= 0; i--)
                {
                    il.InsertBefore(anchor, newInstructions[i]);
                    anchor = newInstructions[i];
                }
            }
            else
            {
                foreach (var instr in newInstructions)
                    il.Append(instr);
            }

            Console.WriteLine($"  Inserted {newInstructions.Length} new instructions");

            // Write patched assembly
            if (inputPath == outputPath)
            {
                assembly.Write();
            }
            else
            {
                assembly.Write(outputPath);
            }

            Console.WriteLine($"Patched: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
