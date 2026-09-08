using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchServiceCompat
{
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes))
                yield return nested;
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException("Usage: PatchServiceCompat.exe original-telemetry.dll output.dll original-server.dll output-server.dll");
        using (var assembly = AssemblyDefinition.ReadAssembly(args[0]))
        {
            int patched = 0;
            foreach (var type in Types(assembly.MainModule.Types))
            foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                var called = instruction.Operand as MethodReference;
                if (called == null || !(
                    (called.DeclaringType.FullName == "Microsoft.Extensions.Logging.GenevaLoggingExtensions" &&
                     called.Name == "AddGenevaLogExporter") ||
                    (called.DeclaringType.FullName == "OpenTelemetry.Exporter.Geneva.GenevaExporterHelperExtensions" &&
                     called.Name == "AddGenevaTraceExporter")))
                    continue;
                if (called.Parameters.Count != 2 || instruction.Next.OpCode != OpCodes.Pop)
                    throw new InvalidOperationException("Unexpected ETW registration IL shape.");
                // Discard its configuration delegate; the existing pop consumes loggerOptions.
                // Other logging providers (including the file exporter) remain available.
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                patched++;
                Console.WriteLine("Linux compatibility: omit Windows-only Geneva ETW registration in " + method.FullName);
            }
            if (patched != 2)
                throw new InvalidOperationException("Expected exactly two Geneva ETW registrations, found " + patched);
            assembly.Write(args[1]);
        }
        using (var assembly = AssemblyDefinition.ReadAssembly(args[2]))
        {
            int patched = 0;
            int monitoring = 0;
            int context = 0;
            foreach (var type in Types(assembly.MainModule.Types))
            foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                var called = instruction.Operand as MethodReference;
                if (called != null && method.Name.Contains("QueueUserWorkItemAndWaitForCompletion"))
                {
                    string contextAdapter = null;
                    if (called.DeclaringType.FullName == "System.Globalization.CultureInfo")
                    {
                        switch (called.Name)
                        {
                            case "set_CurrentCulture": contextAdapter = "SetCulture"; break;
                            case "set_CurrentUICulture": contextAdapter = "SetUiCulture"; break;
                            case "set_DefaultThreadCurrentCulture": contextAdapter = "SetDefaultCulture"; break;
                        }
                    }
                    if (called.DeclaringType.FullName == "System.Threading.Thread" && called.Name == "set_CurrentPrincipal")
                        contextAdapter = "SetPrincipal";
                    if (contextAdapter != null)
                    {
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = assembly.MainModule.ImportReference(typeof(MonoRenderingContext).GetMethod(contextAdapter));
                        context++;
                        continue;
                    }
                }
                if (called != null && called.DeclaringType.FullName == "System.AppDomain" &&
                    called.Name == "set_MonitoringIsEnabled")
                {
                    instruction.OpCode = OpCodes.Pop;
                    instruction.Operand = null;
                    monitoring++;
                    Console.WriteLine("Linux compatibility: skip AppDomain monitoring enable in " + method.FullName);
                    continue;
                }
                if (method.IsConstructor && type.FullName ==
                    "Microsoft.BusinessCentral.Reporting.Server.LocalReportHandle/LocalReportProxy" &&
                    called != null && called.Name == "set_EnableAppDomainMonitor")
                {
                    if (instruction.Previous.OpCode != OpCodes.Ldarg_1)
                        throw new InvalidOperationException("Unexpected monitoring constructor IL.");
                    instruction.Previous.OpCode = OpCodes.Ldc_I4_0;
                    monitoring++;
                    continue;
                }
                if (called == null || called.DeclaringType.FullName != "System.Drawing.Printing.PageSettings")
                    continue;
                string adapter;
                switch (called.Name)
                {
                    case "get_Margins": adapter = "GetMargins"; break;
                    case "get_PaperSize": adapter = "GetPaperSize"; break;
                    case "get_Landscape": adapter = "GetLandscape"; break;
                    default: continue;
                }
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = assembly.MainModule.ImportReference(typeof(HeadlessPageSettings).GetMethod(adapter));
                patched++;
            }
            if (patched == 0)
                throw new InvalidOperationException("No PageSettings getter call sites found.");
            if (monitoring != 3)
                throw new InvalidOperationException("Expected three AppDomain monitoring adaptations, found " + monitoring);
            if (context != 8)
                throw new InvalidOperationException("Expected eight rendering context setter sites, found " + context);
            Console.WriteLine("Linux compatibility: headless RDLC page geometry at {0} getter call sites", patched);
            Console.WriteLine("Linux compatibility: disable unsupported Mono AppDomain resource counters; preserve reporting AppDomain");
            Console.WriteLine("Linux compatibility: rehydrate cross-domain cultures and represent null principal as anonymous");
            assembly.Write(args[3]);
        }
        return 0;
    }
}
