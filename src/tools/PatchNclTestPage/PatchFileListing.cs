using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// DotNet directory listings in AL (issue #183). Every DotNet method call from AL ends in
/// <c>MethodBase.Invoke(object, object[])</c> inside <c>NavDotNet.Invoke&lt;T&gt;</c>. That
/// call becomes a call to <c>BcLinuxFileListing.Invoke</c> (BcLinuxFileListing.cs), which gives
/// the listing methods of Directory and DirectoryInfo Windows' case-insensitive pattern match
/// and name order, and runs every other method as before.
///
/// Idempotent. Exits with an error when the call is not found, because a BC update has then
/// moved the code and the fix would stop working without a sign.
/// </summary>
static class PatchFileListing
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
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".");
            using var assembly = AssemblyDefinition.ReadAssembly(inputPath,
                new ReaderParameters { AssemblyResolver = resolver, ReadWrite = inputPath == outputPath });
            var module = assembly.MainModule;

            var dotNet = module.GetTypes().FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavDotNet");
            var invoke = dotNet?.Methods.FirstOrDefault(m => m.Name == "Invoke" && m.HasBody && m.HasGenericParameters);
            if (invoke == null)
            {
                Console.WriteLine("ERROR: NavDotNet.Invoke<T> not found; the file listing fix cannot be applied");
                return 1;
            }

            int rewritten = 0, already = 0;
            MethodDefinition? helper = null;
            foreach (var ins in invoke.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                if (mr.Name == "Invoke" && mr.DeclaringType?.FullName == "<BcLinuxWindowsText>") { already++; continue; }
                if (ins.OpCode.Code != Code.Callvirt || mr.Name != "Invoke"
                    || mr.DeclaringType?.FullName != "System.Reflection.MethodBase"
                    || mr.Parameters.Count != 2 || mr.Parameters[0].ParameterType.FullName != "System.Object"
                    || mr.Parameters[1].ParameterType.FullName != "System.Object[]") continue;
                helper ??= HelperCloner.Clone(module, "BcLinuxFileListing", "Invoke");
                ins.OpCode = OpCodes.Call;
                ins.Operand = helper;
                rewritten++;
            }

            Console.WriteLine($"Routed {rewritten} MethodBase.Invoke call(s) in NavDotNet.Invoke through the file listing fix in {Path.GetFileName(inputPath)} (already patched: {already})");
            if (rewritten + already == 0)
            {
                Console.WriteLine("ERROR: MethodBase.Invoke not found in NavDotNet.Invoke; BC moved the code and the file listing fix is not applied");
                return 1;
            }
            if (rewritten == 0) return 0;
            if (inputPath == outputPath) assembly.Write(); else assembly.Write(outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
