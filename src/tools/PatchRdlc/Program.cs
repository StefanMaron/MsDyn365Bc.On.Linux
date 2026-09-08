using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: Patcher original.dll output.dll bridge.dll framework-directory");
    return 2;
}
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(args[3]);
using var assembly = AssemblyDefinition.ReadAssembly(args[0],
    new ReaderParameters { AssemblyResolver = resolver });
using var bridge = AssemblyDefinition.ReadAssembly(args[2],
    new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
bool drawingOnly = module.Mvid == Guid.Parse("dda5b535-1d8c-47b3-9b42-906805890c33");
if (!drawingOnly && module.Mvid != Guid.Parse("5b437ccb-9487-4e41-a96f-8f621f811037"))
    throw new InvalidDataException("This prototype only supports the inspected BC 28.4 ReportViewer assembly.");
var dispatch = module.ImportReference(bridge.MainModule.Types
    .Single(t => t.Name == "Bridge").Methods.Single(m => m.Name == "Dispatch"));
if (drawingOnly)
{
    RewriteDrawingCalls();
    assembly.Write(args[1]);
    return 0;
}
var textMethods = new HashSet<string>
{
    "ScriptItemize", "ScriptBreak", "ScriptGetProperties", "ScriptIsComplex",
    "ScriptShape", "ScriptPlace", "ScriptFreeCache", "ScriptGetFontProperties",
    "ScriptLayout", "ScriptCPtoX", "ScriptXtoCP", "ScriptGetLogicalWidths",
    "SetTextAlign", "SetBkMode"
};
var fontTokens = args.Length > 4
    ? args[4].Split(',').Select(s => Convert.ToUInt32(s, 16)).ToHashSet()
    : new HashSet<uint>();
foreach (var type in module.GetTypes())
{
    foreach (var method in type.Methods)
    {
        if (type.FullName == "Microsoft.Reporting.ReportRuntimeSetupHandler" &&
            (method.Name == "get_IsAppDomainCasPolicyEnabled" || method.Name == "get_ExecuteInSandbox"))
        {
            method.Body = new MethodBody(method);
            method.Body.Instructions.Add(Instruction.Create(
                method.Name == "get_IsAppDomainCasPolicyEnabled" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            continue;
        }
        bool text = type.FullName == "Microsoft.ReportingServices.Rendering.RichText.Win32"
            && textMethods.Contains(method.Name);
        bool timer = type.FullName == "Microsoft.ReportingServices.Diagnostics.Timer"
            && method.Name.StartsWith("QueryPerformance", StringComparison.Ordinal);
        bool protection = type.FullName == "Microsoft.ReportingServices.Diagnostics.DataProtectionLocal"
            && (method.Name == "ProtectData" || method.Name == "UnprotectData");
        bool font = fontTokens.Contains(method.MetadataToken.ToUInt32());
        if (!text && !timer && !protection && !font)
            continue;
        Rewrite(method, font ? "font:" + method.MetadataToken.ToUInt32().ToString("X8") : method.Name);
        Console.WriteLine($"Bridge: {method.FullName}");
    }
}
var textRun = module.GetTypes().Single(t =>
    t.FullName == "Microsoft.ReportingServices.Rendering.RichText.TextRun");
var place = textRun.Methods.Single(m => m.Name == "TextScriptPlace");
var placementCalls = place.Body.Instructions.Where(i => i.Operand is MethodReference mr
    && mr.Name == "ScriptPlace").ToArray();
if (placementCalls.Length != 3)
    throw new InvalidDataException("Unexpected original text placement call sites.");
for (int i = 0; i < placementCalls.Length; i++)
{
    var call = placementCalls[i];
    var target = ((MethodReference)call.Operand).Resolve();
    var contextual = new MethodDefinition("LinuxScriptPlace" + i,
        MethodAttributes.Private | MethodAttributes.Static, target.ReturnType);
    foreach (var parameter in target.Parameters)
        contextual.Parameters.Add(new ParameterDefinition(parameter.Name,
            parameter.Attributes, parameter.ParameterType));
    contextual.Parameters.Add(new ParameterDefinition("textRun", ParameterAttributes.None,
        module.TypeSystem.Object));
    textRun.Methods.Add(contextual);
    Rewrite(contextual, "ScriptPlaceRun");
    place.Body.GetILProcessor().InsertBefore(call, Instruction.Create(OpCodes.Ldarg_0));
    call.Operand = contextual;
    call.OpCode = OpCodes.Call;
}
if (fontTokens.Count > 0)
{
    var pdf = module.GetTypes().Single(t =>
        t.FullName == "Microsoft.ReportingServices.Rendering.ImageRenderer.PDFWriter")
        .Methods.Single(m => m.Name == "WriteFont");
    var il = pdf.Body.GetILProcessor();
    var toHandle = pdf.Body.Instructions.Single(i => i.Operand is MethodReference mr
        && mr.DeclaringType.FullName == "System.Drawing.Font" && mr.Name == "ToHfont");
    il.InsertBefore(toHandle, Instruction.Create(OpCodes.Ldarg, pdf.Parameters[0]));
    toHandle.OpCode = OpCodes.Call;
    toHandle.Operand = module.ImportReference(bridge.MainModule.Types
        .Single(t => t.Name == "Bridge").Methods.Single(m => m.Name == "PdfFontToHfont"));
    var angle = pdf.Body.Instructions.Single(i => i.Operand is FieldReference field
        && field.Name == "otmItalicAngle");
    var start = angle.Previous;
    var tail = angle;
    while (tail.OpCode != OpCodes.Conv_I4)
        tail = tail.Next ?? throw new InvalidDataException("Unknown italic-angle expression.");
    start.OpCode = OpCodes.Ldarg;
    start.Operand = pdf.Parameters[0];
    angle.OpCode = OpCodes.Call;
    angle.Operand = module.ImportReference(bridge.MainModule.Types
        .Single(t => t.Name == "Bridge").Methods.Single(m => m.Name == "PdfItalicAngle"));
    for (var instruction = angle.Next; ; instruction = instruction.Next)
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
        if (instruction == tail) break;
    }
    Console.WriteLine("Bridge: original PDF font ownership and italic angle");
}
RewriteDrawingCalls();
assembly.Write(args[1]);
return 0;

void RewriteDrawingCalls()
{
    int count = 0;
    foreach (var type in module.GetTypes().ToArray())
    foreach (var method in type.Methods.ToArray())
    {
        if (!method.HasBody) continue;
        foreach (var instruction in method.Body.Instructions.ToArray())
        {
            if (instruction.Operand is not MethodReference target) continue;
            bool graphics = target.DeclaringType.FullName == "System.Drawing.Graphics" &&
                (target.Name == "DrawString" || target.Name == "MeasureString" ||
                 target.Name == "MeasureCharacterRanges");
            bool height = target.DeclaringType.FullName == "System.Drawing.Font" &&
                target.Name == "GetHeight" && target.Parameters.Count == 1 &&
                target.Parameters[0].ParameterType.FullName == "System.Drawing.Graphics";
            if (!graphics && !height) continue;
            string signature = target.DeclaringType.FullName + "::" + target.Name + "(" +
                string.Join(",", target.Parameters.Select(p => p.ParameterType.FullName)) + ")";
            var wrapper = new MethodDefinition("LinuxDrawing" + count++,
                MethodAttributes.Private | MethodAttributes.Static, target.ReturnType);
            wrapper.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None,
                target.DeclaringType));
            foreach (var parameter in target.Parameters)
                wrapper.Parameters.Add(new ParameterDefinition(parameter.Name,
                    parameter.Attributes & ~ParameterAttributes.HasFieldMarshal, parameter.ParameterType));
            type.Methods.Add(wrapper);
            Rewrite(wrapper, "drawing:" + signature);
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = wrapper;
        }
    }
    Console.WriteLine("DPI-aware raster text call sites: " + count);
}

void Rewrite(MethodDefinition method, string id)
{
    method.PInvokeInfo = null;
    method.IsPInvokeImpl = false;
    method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
    method.Body = new MethodBody(method) { InitLocals = true, MaxStackSize = 16 };
    var il = method.Body.GetILProcessor();
    var values = new VariableDefinition(new ArrayType(module.TypeSystem.Object));
    var result = new VariableDefinition(module.TypeSystem.Object);
    method.Body.Variables.Add(values);
    method.Body.Variables.Add(result);
    il.Emit(OpCodes.Ldc_I4, method.Parameters.Count);
    il.Emit(OpCodes.Newarr, module.TypeSystem.Object);
    il.Emit(OpCodes.Stloc, values);
    for (int i = 0; i < method.Parameters.Count; i++)
    {
        var p = method.Parameters[i];
        var valueType = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
        il.Emit(OpCodes.Ldloc, values);
        il.Emit(OpCodes.Ldc_I4, i);
        if (p.IsOut && !p.IsIn && p.ParameterType.IsByReference)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Ldarg, p);
            if (p.ParameterType.IsByReference)
                il.Emit(OpCodes.Ldobj, valueType);
            if (valueType.IsValueType || valueType.IsGenericParameter)
                il.Emit(OpCodes.Box, valueType);
        }
        il.Emit(OpCodes.Stelem_Ref);
    }
    il.Emit(OpCodes.Ldstr, id);
    if (method.IsStatic)
        il.Emit(OpCodes.Ldnull);
    else
    {
        il.Emit(OpCodes.Ldarg_0);
        if (method.DeclaringType.IsValueType)
        {
            il.Emit(OpCodes.Ldobj, method.DeclaringType);
            il.Emit(OpCodes.Box, method.DeclaringType);
        }
    }
    il.Emit(OpCodes.Ldloc, values);
    il.Emit(OpCodes.Call, dispatch);
    il.Emit(OpCodes.Stloc, result);
    for (int i = 0; i < method.Parameters.Count; i++)
    {
        var p = method.Parameters[i];
        if (p.ParameterType is not ByReferenceType byRef)
            continue;
        il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Ldloc, values);
        il.Emit(OpCodes.Ldc_I4, i);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(byRef.ElementType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, byRef.ElementType);
        il.Emit(OpCodes.Stobj, byRef.ElementType);
    }
    if (method.ReturnType.MetadataType != MetadataType.Void)
    {
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType);
    }
    il.Emit(OpCodes.Ret);
}
