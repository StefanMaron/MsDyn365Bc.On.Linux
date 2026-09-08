using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Patches TestPageClient.dll: changes CommunicationBroker.DefaultChannelOptions.Async
/// from true to false in TestPageClientSession.Create().
///
/// OPT-IN ONLY as of issue #78 (scripts/entrypoint.sh gates this behind
/// BC_TESTPAGE_ASYNC_PATCH=1; the default no longer applies it). Async=true is what lets
/// BC coalesce rapid-fire PropertyChanged/CurrentRowChanged notifications during a
/// page/part fill — CommunicationChannel.EnsureQueueLength evicts messages past
/// ChannelOptions.QueueLength, and BindingManagerConsumerPort.FillStarting pulls only one
/// survivor per channel per fill. Forcing Async=false sends every notification inline
/// instead, with nothing to evict — measured as an empty linked part's draft row raising
/// OnNewRecord twice as often here as on a real BC sandbox tier (6 vs 3, confirmed
/// against an online sandbox; same shape, one draft row re-notified, not extra rows).
///
/// The deadlock this patch was originally written to avoid (commit 29d2bdf) was never
/// isolated from that commit's OTHER fix in the same change (ToUnicodeEx returning 0
/// instead of 1, which independently caused an infinite loop in
/// KeyboardMapper.ClearKeyboardBuffer during page form building) — and at the time
/// TestPage couldn't run end-to-end anyway (NavSession.CreateNavTestService() still threw
/// NotSupportedException). Re-tested with Async left at true against a RunObject action
/// to a StandardDialog target answered by [ModalPageHandler] — the shape most likely to
/// need a real pump: 7/7 pass, no hang. Kept as an escape hatch, not removed, in case some
/// TestPage scenario not yet exercised does need it.
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
