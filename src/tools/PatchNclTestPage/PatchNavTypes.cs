using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Patches Nav.Types.dll to fix TestClientProxy Assembly.Load calls.
///
/// TestClientProxy&lt;T&gt;.GetApplyTestLogicalDispatcherOnTls() and
/// GetTestLogicalDispatcher() use Assembly.Load(qualifiedName) to load
/// TestPageClient.dll. On Linux, this blocks forever in the ALC resolver.
///
/// Fix: Replace Assembly.Load(qualifiedName) with Assembly.LoadFrom(filePath)
/// using the executing assembly's directory, same as the Nav.Ncl.dll patch.
/// </summary>
static class PatchNavTypes
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

            // Find TestClientProxy<T> type
            var type = module.GetTypes().FirstOrDefault(t => t.Name.StartsWith("TestClientProxy"));
            if (type == null)
            {
                Console.WriteLine("ERROR: TestClientProxy type not found");
                return 1;
            }
            Console.WriteLine($"Found {type.FullName}");

            // Patch both GetApplyTestLogicalDispatcherOnTls and GetTestLogicalDispatcher
            int patchCount = 0;
            foreach (var method in type.Methods)
            {
                if (method.Name != "GetApplyTestLogicalDispatcherOnTls" &&
                    method.Name != "GetTestLogicalDispatcher")
                    continue;

                Console.WriteLine($"  Patching {method.Name}...");
                if (PatchAssemblyLoadInMethod(module, method))
                    patchCount++;
            }

            if (patchCount == 0)
            {
                Console.WriteLine("ERROR: No methods patched");
                return 1;
            }

            if (inputPath == outputPath)
                assembly.Write();
            else
                assembly.Write(outputPath);

            Console.WriteLine($"Patched {patchCount} methods: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static bool PatchAssemblyLoadInMethod(ModuleDefinition module, MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        var instructions = method.Body.Instructions;

        // Find Assembly.Load(string) call
        Instruction? loadCall = null;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                && mr.Name == "Load" && mr.DeclaringType.Name == "Assembly"
                && mr.Parameters.Count == 1)
            {
                loadCall = instr;
                break;
            }
        }

        if (loadCall == null)
        {
            Console.WriteLine("    WARNING: Assembly.Load not found");
            return false;
        }

        // Find GetExecutingAssembly (start of the string building sequence)
        Instruction? getExecAsm = null;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                && mr.Name == "GetExecutingAssembly")
            {
                getExecAsm = instr;
                break;
            }
        }

        if (getExecAsm == null)
        {
            Console.WriteLine("    WARNING: GetExecutingAssembly not found");
            return false;
        }

        // Import methods
        var loadFromMethod = module.ImportReference(
            typeof(System.Reflection.Assembly).GetMethod("LoadFrom", new[] { typeof(string) }));
        var getExecAsmMethod = module.ImportReference(
            typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly")!);
        var getLocationMethod = module.ImportReference(
            typeof(System.Reflection.Assembly).GetProperty("Location")!.GetGetMethod()!);
        var pathCombineMethod = module.ImportReference(
            typeof(System.IO.Path).GetMethod("Combine", new[] { typeof(string), typeof(string) }));
        var pathGetDirMethod = module.ImportReference(
            typeof(System.IO.Path).GetMethod("GetDirectoryName", new[] { typeof(string) }));

        // Remove GetExecutingAssembly..Assembly.Load sequence
        int startIdx = instructions.IndexOf(getExecAsm);
        int endIdx = instructions.IndexOf(loadCall);

        var toRemove = new System.Collections.Generic.List<Instruction>();
        for (int i = startIdx; i <= endIdx; i++)
            toRemove.Add(instructions[i]);

        var insertBefore = (endIdx + 1 < instructions.Count) ? instructions[endIdx + 1] : null;

        foreach (var instr in toRemove)
            il.Remove(instr);

        // Insert: Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "TestPageClient.dll"))
        var newInstructions = new[]
        {
            il.Create(OpCodes.Call, getExecAsmMethod),
            il.Create(OpCodes.Callvirt, getLocationMethod),
            il.Create(OpCodes.Call, pathGetDirMethod),
            il.Create(OpCodes.Ldstr, "Microsoft.Dynamics.Nav.Client.TestPageClient.dll"),
            il.Create(OpCodes.Call, pathCombineMethod),
            il.Create(OpCodes.Call, loadFromMethod),
        };

        if (insertBefore != null)
        {
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

        Console.WriteLine($"    Replaced {toRemove.Count} instructions with {newInstructions.Length}");
        return true;
    }
}
