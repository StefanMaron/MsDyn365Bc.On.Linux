using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Paths under the session's user folder that BC builds with '\' (issues #175, #176, #181).
///
/// BC builds TemporaryPath() as Path.Combine(userFolder, "TEMP\"). On Windows that is
/// "...\TEMP\", and AL code sees that value. On Linux Path.Combine keeps the backslash as an
/// ordinary character. AL's File functions turn '\' into '/' (Patch #28), so they work, but two
/// places hand the raw string to System.IO and get a different answer than on Windows:
///
///  * DotNet calls. A DotNet DirectoryInfo or Directory.GetFiles given "...\TEMP\sub" looks for
///    a file whose name contains backslashes and finds nothing. The AL value is converted to a
///    .NET argument in NavDotNet.GetDotNetObjectFromNavObject; the text results there go through
///    <c>NewDotNetPathArgument</c>, which turns '\' into '/' when the text starts with the
///    session's user folder. Text that is not under the user folder is passed on unchanged.
///  * Text that DotNet returns to AL goes the other way: a path under the user folder comes back
///    with '\' after the user folder, as on Windows (<c>NewDotNetPathResult</c>, called first in
///    NavDotNet.GetNavObjectFromDotNetObject). The separator right after the user folder stays as
///    Path.Combine wrote it, so a path DotNet returns equals TemporaryPath() + 'sub\a.xml'.
///  * MediaSet.ImportFile takes the file name with Path.GetFileName, which on Linux only splits
///    at '/', so "TEMP\name.jpg" was stored as the name. Windows splits at both. The calls in
///    NavMediaSet go through <c>FileNameAtEitherSeparator</c>.
///
/// TemporaryPath() itself is left alone: AL code sees the Windows value ("...TEMP\").
/// Idempotent. Exits with an error when a place to patch is not found, because that means a BC
/// update moved the code and the fix would otherwise stop working without a sign.
/// </summary>
static class PatchTempPath
{
    const string HelperType = "<BcLinuxWindowsText>";
    const string ArgumentHelper = "NewDotNetPathArgument";
    const string FileNameHelper = "FileNameAtEitherSeparator";
    const string ResultHelper = "NewDotNetPathResult";
    static readonly string[] TextTypes = { "NavStringValue" };

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
            var convert = dotNet?.Methods.FirstOrDefault(m => m.Name == "GetDotNetObjectFromNavObject" && m.HasBody);
            if (convert == null)
            {
                Console.WriteLine("ERROR: NavDotNet.GetDotNetObjectFromNavObject not found; the DotNet path fix cannot be applied");
                return 1;
            }

            MethodDefinition? argumentHelper = null, fileNameHelper = null;
            int arguments = 0, alreadyArguments = 0, fileNames = 0, alreadyFileNames = 0;

            var ins = convert.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Call && ins[i].OpCode.Code != Code.Callvirt) continue;
                if (ins[i].Operand is not MethodReference mr || mr.Name != "get_Value"
                    || !TextTypes.Contains(mr.DeclaringType?.Name)) continue;
                if (ins[i + 1].Operand is MethodReference next && next.Name == ArgumentHelper)
                {
                    alreadyArguments++;
                    continue;
                }
                argumentHelper ??= AddArgumentHelper(module);
                convert.Body.GetILProcessor().InsertAfter(ins[i], Instruction.Create(OpCodes.Call, argumentHelper));
                arguments++;
            }
            if (arguments > 0) IlHelpers.LengthenShortBranches(convert.Body);

            var toNav = dotNet.Methods.FirstOrDefault(m => m.Name == "GetNavObjectFromDotNetObject" && m.HasBody
                && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.FullName == "System.Object");
            if (toNav == null)
            {
                Console.WriteLine("ERROR: NavDotNet.GetNavObjectFromDotNetObject not found; the DotNet path result fix cannot be applied");
                return 1;
            }
            int results = 0, alreadyResults = 0;
            var firstIns = toNav.Body.Instructions[0];
            if (firstIns.Operand is MethodReference firstRef && firstRef.Name == ResultHelper
                || toNav.Body.Instructions.Take(4).Any(i => i.Operand is MethodReference r && r.Name == ResultHelper))
                alreadyResults++;
            else
            {
                var resultHelper = AddResultHelper(module);
                var il = toNav.Body.GetILProcessor();
                var valueParam = toNav.Parameters[1];
                il.InsertBefore(firstIns, il.Create(OpCodes.Ldarg, valueParam));
                il.InsertBefore(firstIns, il.Create(OpCodes.Call, resultHelper));
                il.InsertBefore(firstIns, il.Create(OpCodes.Starg, valueParam));
                IlHelpers.LengthenShortBranches(toNav.Body);
                results++;
            }

            var mediaSet = module.GetTypes().Where(t => OuterName(t) == "NavMediaSet").ToList();
            foreach (var type in mediaSet)
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    bool grown = false;
                    foreach (var call in method.Body.Instructions)
                    {
                        if (call.OpCode.Code != Code.Call || call.Operand is not MethodReference mr) continue;
                        if (mr.Name == FileNameHelper) { alreadyFileNames++; continue; }
                        if (mr.Name != "GetFileName" || mr.DeclaringType?.FullName != "System.IO.Path"
                            || mr.Parameters.Count != 1 || mr.Parameters[0].ParameterType.FullName != "System.String") continue;
                        fileNameHelper ??= AddFileNameHelper(module);
                        call.Operand = fileNameHelper;
                        fileNames++;
                        grown = true;
                    }
                    if (grown) IlHelpers.LengthenShortBranches(method.Body);
                }

            Console.WriteLine($"Patched {arguments} DotNet text argument(s), {results} DotNet result conversion and {fileNames} MediaSet GetFileName call(s) in {Path.GetFileName(inputPath)} "
                + $"(already patched: {alreadyArguments}, {alreadyResults} and {alreadyFileNames})");
            if (arguments + alreadyArguments == 0 || fileNames + alreadyFileNames == 0)
            {
                Console.WriteLine("ERROR: a place to patch was not found; BC moved the code and the temp path fix is not applied");
                return 1;
            }
            if (arguments + fileNames + results == 0) return 0;
            if (inputPath == outputPath) assembly.Write(); else assembly.Write(outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static string OuterName(TypeDefinition type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type.Name;
    }

    static TypeDefinition Holder(ModuleDefinition module)
    {
        var holder = module.Types.FirstOrDefault(t => t.Name == HelperType);
        if (holder != null) return holder;
        holder = new TypeDefinition("", HelperType,
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.NotPublic,
            module.TypeSystem.Object);
        module.Types.Add(holder);
        return holder;
    }

    /// <summary>
    /// static string NewDotNetPathArgument(string s): s with '\' turned into '/' when it starts
    /// with the session's user folder, else s.
    /// </summary>
    static MethodDefinition AddArgumentHelper(ModuleDefinition module)
    {
        var holder = Holder(module);
        var existing = holder.Methods.FirstOrDefault(m => m.Name == ArgumentHelper);
        if (existing != null) return existing;

        var currentThread = module.GetTypes().First(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
        var session = module.GetTypes().First(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession");
        var getSession = module.ImportReference(currentThread.Methods.First(m => m.Name == "get_Session"));
        var getUserFolder = module.ImportReference(session.Methods.First(m => m.Name == "get_UserFolder"));
        var indexOf = module.ImportReference(typeof(string).GetMethod("IndexOf", new[] { typeof(char) }));
        var isNullOrEmpty = module.ImportReference(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }));
        var startsWith = module.ImportReference(typeof(string).GetMethod("StartsWith", new[] { typeof(string), typeof(StringComparison) }));
        var replace = module.ImportReference(typeof(string).GetMethod("Replace", new[] { typeof(char), typeof(char) }));

        var m = new MethodDefinition(ArgumentHelper,
            MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.String);
        m.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, module.TypeSystem.String));
        holder.Methods.Add(m);
        m.Body.InitLocals = true;
        m.Body.Variables.Add(new VariableDefinition(module.ImportReference(session)));
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));

        var il = m.Body.GetILProcessor();
        var unchanged = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'\\'));
        il.Append(il.Create(OpCodes.Callvirt, indexOf));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Blt, unchanged));
        il.Append(il.Create(OpCodes.Call, getSession));
        il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Callvirt, getUserFolder));
        il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Call, isNullOrEmpty));
        il.Append(il.Create(OpCodes.Brtrue, unchanged));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Ldc_I4_4)); // StringComparison.Ordinal
        il.Append(il.Create(OpCodes.Callvirt, startsWith));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'\\'));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(il.Create(OpCodes.Callvirt, replace));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(unchanged);
        il.Append(il.Create(OpCodes.Ret));
        return m;
    }

    /// <summary>
    /// static object NewDotNetPathResult(object value): when value is text that starts with the
    /// session's user folder, every '/' after the user folder (and the separator right after it)
    /// becomes '\\'; anything else is returned as it came.
    /// </summary>
    static MethodDefinition AddResultHelper(ModuleDefinition module)
    {
        var holder = Holder(module);
        var existing = holder.Methods.FirstOrDefault(m => m.Name == ResultHelper);
        if (existing != null) return existing;

        var currentThread = module.GetTypes().First(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
        var session = module.GetTypes().First(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession");
        var getSession = module.ImportReference(currentThread.Methods.First(m => m.Name == "get_Session"));
        var getUserFolder = module.ImportReference(session.Methods.First(m => m.Name == "get_UserFolder"));
        var isNullOrEmpty = module.ImportReference(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }));
        var startsWith = module.ImportReference(typeof(string).GetMethod("StartsWith", new[] { typeof(string), typeof(StringComparison) }));
        var getLength = module.ImportReference(typeof(string).GetMethod("get_Length"));
        var getChars = module.ImportReference(typeof(string).GetMethod("get_Chars", new[] { typeof(int) }));
        var substring1 = module.ImportReference(typeof(string).GetMethod("Substring", new[] { typeof(int) }));
        var substring2 = module.ImportReference(typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) }));
        var replace = module.ImportReference(typeof(string).GetMethod("Replace", new[] { typeof(char), typeof(char) }));
        var concat = module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));

        var m = new MethodDefinition(ResultHelper,
            MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Object);
        m.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
        holder.Methods.Add(m);
        m.Body.InitLocals = true;
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));          // 0: s
        m.Body.Variables.Add(new VariableDefinition(module.ImportReference(session)));  // 1: session
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));          // 2: folder
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));           // 3: keep

        var il = m.Body.GetILProcessor();
        var unchanged = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Isinst, module.TypeSystem.String));
        il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        il.Append(il.Create(OpCodes.Call, getSession));
        il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Callvirt, getUserFolder));
        il.Append(il.Create(OpCodes.Stloc_2));
        il.Append(il.Create(OpCodes.Ldloc_2));
        il.Append(il.Create(OpCodes.Call, isNullOrEmpty));
        il.Append(il.Create(OpCodes.Brtrue, unchanged));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldloc_2));
        il.Append(il.Create(OpCodes.Ldc_I4_4)); // StringComparison.Ordinal
        il.Append(il.Create(OpCodes.Callvirt, startsWith));
        il.Append(il.Create(OpCodes.Brfalse, unchanged));
        // keep = folder.Length; if (keep < s.Length && (s[keep] == '/' || s[keep] == '\\')) keep++;
        var convert = il.Create(OpCodes.Ldloc_0);
        var bump = il.Create(OpCodes.Ldloc_3);
        il.Append(il.Create(OpCodes.Ldloc_2));
        il.Append(il.Create(OpCodes.Callvirt, getLength));
        il.Append(il.Create(OpCodes.Stloc_3));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Callvirt, getLength));
        il.Append(il.Create(OpCodes.Bge, convert));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Callvirt, getChars));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(il.Create(OpCodes.Beq, bump));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Callvirt, getChars));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'\\'));
        il.Append(il.Create(OpCodes.Bne_Un, convert));
        il.Append(bump);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc_3));
        // return s.Substring(0, keep) + s.Substring(keep).Replace('/', '\\');
        il.Append(convert);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Callvirt, substring2));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Callvirt, substring1));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'\\'));
        il.Append(il.Create(OpCodes.Callvirt, replace));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(unchanged);
        il.Append(il.Create(OpCodes.Ret));
        return m;
    }

    /// <summary>static string FileNameAtEitherSeparator(string s): the part after the last '/' or '\'.</summary>
    static MethodDefinition AddFileNameHelper(ModuleDefinition module)
    {
        var holder = Holder(module);
        var existing = holder.Methods.FirstOrDefault(m => m.Name == FileNameHelper);
        if (existing != null) return existing;

        var lastIndexOf = module.ImportReference(typeof(string).GetMethod("LastIndexOf", new[] { typeof(char) }));
        var substring = module.ImportReference(typeof(string).GetMethod("Substring", new[] { typeof(int) }));

        var m = new MethodDefinition(FileNameHelper,
            MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.String);
        m.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, module.TypeSystem.String));
        holder.Methods.Add(m);
        m.Body.InitLocals = true;
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));

        var il = m.Body.GetILProcessor();
        var nullResult = il.Create(OpCodes.Ldnull);
        var keep = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Brfalse, nullResult));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(il.Create(OpCodes.Callvirt, lastIndexOf));
        il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)'\\'));
        il.Append(il.Create(OpCodes.Callvirt, lastIndexOf));
        il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Bge, keep));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(keep);
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(nullResult);
        il.Append(il.Create(OpCodes.Ret));
        return m;
    }
}
