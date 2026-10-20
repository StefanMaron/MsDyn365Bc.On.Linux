using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Windows culture data for the formatting culture (issue #113). The rules live in
/// BcLinuxCulture.cs; this patch copies that class into the target assembly (Cecil reads the
/// patcher's own DLL and clones the IL) and routes BC's format-culture creation through it:
///
///   every CultureInfo.GetCultureInfo(...) result in the assembly goes through Fix
///   every new CultureInfo(lcid|name) goes through FixWritable (in place, the instance is writable)
///   ClientSettings.Refresh:   the formatCulture argument goes through Fix first
///   TimeZoneDataProvider:     GetSystemTimeZones() becomes the Windows list (BcLinuxTimeZones.cs)
///
/// Idempotent: a call already followed by Fix, or a Refresh that already starts with it,
/// is left alone.
/// </summary>
static class PatchCulture
{
    const string HolderType = HelperCloner.HolderType;

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

            MethodDefinition fix = null;
            int patched = 0;

            foreach (var type in module.GetTypes().ToList())
            {
                if (type.Name == HolderType) continue;

                // The virtual table "Time Zone" (2000000164) lists GetSystemTimeZones(): the
                // Windows zones on Windows, the IANA zones here (issue #132). Its iterator gets the
                // Windows list instead. Same signature, so the call is swapped in place.
                if (type.Name == "TimeZoneDataProvider" || type.DeclaringType?.Name == "TimeZoneDataProvider")
                {
                    foreach (var method in type.Methods.Where(m => m.HasBody))
                    {
                        foreach (var ins in method.Body.Instructions)
                        {
                            if (ins.OpCode.Code != Code.Call || ins.Operand is not MethodReference tz) continue;
                            if (tz.Name != "GetSystemTimeZones" || tz.Parameters.Count != 0
                                || tz.DeclaringType?.FullName != "System.TimeZoneInfo") continue;
                            ins.Operand = HelperCloner.Clone(module, "BcLinuxTimeZones", "WindowsTimeZones");
                            patched++;
                        }
                    }
                }

                // Every CultureInfo.GetCultureInfo(...) result in BC's code: the format culture,
                // the session culture, the report's format region, parsing cultures.
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    var il = method.Body.GetILProcessor();
                    bool grown = false;
                    foreach (var ins in method.Body.Instructions.ToList())
                    {
                        if (ins.Operand is not MethodReference mr) continue;
                        if (mr.DeclaringType?.FullName != "System.Globalization.CultureInfo") continue;

                        if (ins.OpCode.Code == Code.Newobj && mr.Name == ".ctor" && mr.Parameters.Count == 1
                            && mr.Parameters[0].ParameterType.FullName is "System.Int32" or "System.String")
                        {
                            // new CultureInfo(lcid | name): a writable instance, normalized in place.
                            if (ins.Next?.Operand is MethodReference nw && nw.Name == "BcLinuxCulture_FixWritable") continue;
                            fix ??= HelperCloner.Clone(module, "BcLinuxCulture", "Fix");
                            var fixWritable = fix.DeclaringType.Methods.First(x => x.Name == "BcLinuxCulture_FixWritable");
                            il.InsertAfter(ins, il.Create(OpCodes.Call, fixWritable));
                            patched++;
                            grown = true;
                            continue;
                        }

                        if (mr.Name != "GetCultureInfo") continue;
                        if (mr.ReturnType.FullName != "System.Globalization.CultureInfo") continue;
                        if (ins.Next?.Operand is MethodReference n && n.Name == "Fix") continue;
                        fix ??= HelperCloner.Clone(module, "BcLinuxCulture", "Fix");
                        il.InsertAfter(ins, il.Create(OpCodes.Call, fix));
                        patched++;
                        grown = true;
                    }
                    if (grown) IlHelpers.LengthenShortBranches(method.Body);
                }

                // ClientSettings copies a culture's patterns into plain strings.
                if (type.Name == "ClientSettings")
                {
                    foreach (var method in type.Methods.Where(m => m.HasBody && m.Name == "Refresh"
                        && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Globalization.CultureInfo"
                        && m.Parameters[1].ParameterType.FullName == "System.Int32"))
                    {
                        var first = method.Body.Instructions[0];
                        if (first.Next?.Operand is MethodReference n && n.Name == "Fix") continue;
                        fix ??= HelperCloner.Clone(module, "BcLinuxCulture", "Fix");
                        var il = method.Body.GetILProcessor();
                        // formatCulture = Fix(formatCulture), placed before the old first instruction
                        // so a branch to it still runs the new prologue.
                        il.InsertBefore(first, il.Create(OpCodes.Ldarg, method.Parameters[0]));
                        il.InsertBefore(first, il.Create(OpCodes.Call, fix));
                        il.InsertBefore(first, il.Create(OpCodes.Starg, method.Parameters[0]));
                        IlHelpers.LengthenShortBranches(method.Body);
                        patched++;
                    }
                }
            }

            Console.WriteLine($"Windows culture data: {patched} places routed through the culture fix in {Path.GetFileName(inputPath)}");
            if (patched == 0) return 0;
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
