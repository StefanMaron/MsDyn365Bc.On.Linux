using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using BcLinux.Authentication;
using Mono.Cecil;

const string DefaultType = "Microsoft.Dynamics.Nav.Runtime.NavUser";
const string DefaultMethod = "TryAuthenticate";

try
{
    return args.FirstOrDefault() switch
    {
        "generate" => Generate(args.Skip(1).ToArray()),
        "self-test" => SelfTest(),
        _ => Inspect(args),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"NavUserPasswordInspector: {ex.Message}");
    return 1;
}

static int Generate(string[] args)
{
    var userSecurityIdText = GetOption(args, "--user-security-id")
        ?? throw new ArgumentException("generate requires --user-security-id <guid>");
    if (!Guid.TryParse(userSecurityIdText, out var userSecurityId))
        throw new ArgumentException("--user-security-id is not a valid GUID");

    // The password deliberately travels over stdin, never argv or a temporary file.
    var password = Console.In.ReadToEnd();
    if (password.Length == 0)
        throw new ArgumentException("password on stdin must not be empty");

    Console.Out.Write(V3Password.GenerateStored(password, userSecurityId));
    return 0;
}

static int SelfTest()
{
    const string expected = "aXD91GRctWiXaqXeWbXhxQ==-V3";
    var actual = V3Password.GenerateStored(
        "Admin123!", Guid.Parse("00000000-0000-0000-0000-000000000001"));
    if (actual != expected)
        throw new InvalidOperationException("BC V3 historical vector did not match");

    Console.WriteLine("BC V3 historical vector: PASS");
    return 0;
}

static int Inspect(string[] args)
{
    var serviceDir = GetOption(args, "--service-dir")
        ?? throw new ArgumentException("inspection requires --service-dir <directory>");
    var typeName = GetOption(args, "--type") ?? DefaultType;
    var methodName = GetOption(args, "--method") ?? DefaultMethod;
    var assemblyPath = GetOption(args, "--assembly")
        ?? Path.Combine(serviceDir, "Microsoft.Dynamics.Nav.Ncl.dll");

    assemblyPath = Path.GetFullPath(assemblyPath);
    serviceDir = Path.GetFullPath(serviceDir);
    if (!File.Exists(assemblyPath))
        throw new FileNotFoundException("assembly was not found", assemblyPath);

    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(serviceDir);
    resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath)!);
    using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
    {
        AssemblyResolver = resolver,
        ReadSymbols = false,
        ReadingMode = ReadingMode.Deferred,
        InMemory = true,
    });

    var file = FileVersionInfo.GetVersionInfo(assemblyPath);
    var targetFramework = assembly.CustomAttributes
        .FirstOrDefault(a => a.AttributeType.FullName == typeof(System.Runtime.Versioning.TargetFrameworkAttribute).FullName)
        ?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "<none>";

    Console.WriteLine($"Path: {assemblyPath}");
    Console.WriteLine($"SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant()}");
    Console.WriteLine($"Assembly: {assembly.Name.FullName}");
    Console.WriteLine($"FileVersion: {file.FileVersion ?? "<none>"}");
    Console.WriteLine($"TargetFramework: {targetFramework}");
    Console.WriteLine($"MVID: {assembly.MainModule.Mvid:D}");

    var type = assembly.MainModule.GetType(typeName)
        ?? assembly.MainModule.Types.SelectMany(Flatten).FirstOrDefault(t => t.FullName == typeName)
        ?? throw new InvalidOperationException($"type not found: {typeName}");
    var methods = type.Methods.Where(m => m.Name == methodName).ToArray();
    if (methods.Length == 0)
        throw new InvalidOperationException($"method not found: {typeName}.{methodName}");

    foreach (var method in methods)
    {
        Console.WriteLine();
        Console.WriteLine($"Method: {method.FullName}");
        Console.WriteLine($"MetadataToken: {method.MetadataToken}");
        if (!method.HasBody)
        {
            Console.WriteLine("  <no method body>");
            continue;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            var operand = instruction.Operand switch
            {
                MethodReference called => called.FullName,
                FieldReference field => field.FullName,
                TypeReference referencedType => referencedType.FullName,
                null => "",
                _ => instruction.Operand.ToString() ?? "",
            };
            Console.WriteLine($"  IL_{instruction.Offset:x4}: {instruction.OpCode.Name,-12} {operand}");
        }
    }

    return 0;
}

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(Flatten))
        yield return nested;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
        if (args[i] == name && i + 1 < args.Length)
            return args[i + 1];
    return null;
}
