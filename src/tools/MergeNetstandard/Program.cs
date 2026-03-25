using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

class Program
{
    static readonly string BaseDir = Environment.GetEnvironmentVariable("BASE_DIR")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static readonly string RefAsmDir = Path.Combine(BaseDir, "StartupHook/refasm");
    static readonly string PatchedDir = Path.Combine(BaseDir, "StartupHook/patched");
    static readonly string PlatformDir = Environment.GetEnvironmentVariable("PLATFORM_DIR")
        ?? Path.Combine(BaseDir, "artifacts/onprem/27.5.46862.0/platform");
    static readonly string ServiceTierDir = FindServiceTierDir(PlatformDir);
    static readonly string WebClientRefsDir = FindWebClientRefsDir(PlatformDir);

    // Search directories for resolving target assemblies (in priority order)
    static readonly string[] SearchDirs = new[]
    {
        ServiceTierDir,
        RefAsmDir,
        WebClientRefsDir,
    };

    static string FindServiceTierDir(string platformDir)
    {
        // Search for the service tier directory containing Microsoft.Dynamics.Nav.Server.dll
        foreach (var dll in Directory.GetFiles(platformDir, "Microsoft.Dynamics.Nav.Server.dll", SearchOption.AllDirectories))
            return Path.GetDirectoryName(dll)!;
        return Path.Combine(platformDir, "ServiceTier"); // fallback
    }

    static string FindWebClientRefsDir(string platformDir)
    {
        // Search for the WebClient refs directory
        foreach (var dir in Directory.GetDirectories(platformDir, "refs", SearchOption.AllDirectories))
            if (dir.Contains("WebClient") || dir.Contains("WebPublish"))
                return dir;
        return Path.Combine(platformDir, "WebClient"); // fallback
    }

    static void Main(string[] args)
    {
        Directory.CreateDirectory(PatchedDir);

        // 1. Merge netstandard.dll
        MergeAssembly(
            sourcePath: Path.Combine(RefAsmDir, "netstandard.dll"),
            outputPath: Path.Combine(PatchedDir, "netstandard-merged.dll"),
            extraSearchDirs: null);

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 2. Merge DocumentFormat.OpenXml.dll
        MergeAssembly(
            sourcePath: Path.Combine(ServiceTierDir, "DocumentFormat.OpenXml.dll"),
            outputPath: Path.Combine(PatchedDir, "DocumentFormat.OpenXml-merged.dll"),
            extraSearchDirs: new[] { ServiceTierDir });

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 3. Merge System.Drawing.dll
        MergeAssembly(
            sourcePath: Path.Combine(WebClientRefsDir, "System.Drawing.dll"),
            outputPath: Path.Combine(PatchedDir, "System.Drawing-merged.dll"),
            extraSearchDirs: new[] { WebClientRefsDir });

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 4. Merge System.Core.dll
        MergeAssembly(
            sourcePath: Path.Combine(WebClientRefsDir, "System.Core.dll"),
            outputPath: Path.Combine(PatchedDir, "System.Core-merged.dll"),
            extraSearchDirs: new[] { WebClientRefsDir });

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 5. Post-process: Convert refasm TypeDefinitions to type-forwards (ExportedTypes)
        // pointing to netstandard, so Cecil's Resolve() lands on the same TypeDefinition
        // as the AL code's GetType() on netstandard-merged.dll.
        RedirectRefasmToNetstandard();

        Console.WriteLine("\nAll merges complete.");
    }

    /// <summary>
    /// For each refasm assembly, create a new "forwarding" assembly that type-forwards
    /// ALL types to netstandard. This replaces the original refasm assembly in the
    /// patched output directory. When Cecil's Resolve() follows a BC DLL TypeReference,
    /// it finds the forwarding assembly, follows the ExportedType to netstandard-merged.dll,
    /// and lands on the same TypeDefinition that GetType() finds for AL DotNet types.
    /// </summary>
    static void RedirectRefasmToNetstandard()
    {
        var mergedPath = Path.Combine(PatchedDir, "netstandard-merged.dll");
        if (!File.Exists(mergedPath))
        {
            Console.WriteLine("  SKIP: netstandard-merged.dll not found");
            return;
        }

        // Load merged assembly to get the set of types it defines
        using var mergedAsm = AssemblyDefinition.ReadAssembly(mergedPath, new ReaderParameters { ReadSymbols = false });
        var mergedTypes = new HashSet<string>();
        CollectTypeNames(mergedAsm.MainModule.Types, mergedTypes);

        Console.WriteLine($"Netstandard-merged has {mergedTypes.Count} types available for redirects");

        // Create the forwarding output directory
        var forwardDir = Path.Combine(PatchedDir, "refasm-forwarding");
        Directory.CreateDirectory(forwardDir);

        var refAsmFiles = Directory.GetFiles(RefAsmDir, "*.dll");
        int totalRedirected = 0;

        foreach (var refPath in refAsmFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(refPath);
            // Skip assemblies we replace entirely
            if (fileName is "netstandard") continue;

            try
            {
                using var refAsm = AssemblyDefinition.ReadAssembly(refPath, new ReaderParameters { ReadSymbols = false });
                var module = refAsm.MainModule;

                // Find top-level types that are ALSO in netstandard-merged.dll
                var typesToForward = new List<TypeDefinition>();
                foreach (var type in module.Types)
                {
                    if (type.Name == "<Module>") continue;
                    if (mergedTypes.Contains(type.FullName))
                        typesToForward.Add(type);
                }

                if (typesToForward.Count == 0) continue;

                // Create a new assembly with ONLY type-forwards for the matching types
                var newAsm = AssemblyDefinition.CreateAssembly(
                    refAsm.Name, refAsm.Name.Name, ModuleKind.Dll);
                var newModule = newAsm.MainModule;

                // Add netstandard assembly reference
                var netstdRef = new AssemblyNameReference("netstandard", mergedAsm.Name.Version)
                {
                    PublicKeyToken = mergedAsm.Name.PublicKeyToken
                };
                newModule.AssemblyReferences.Add(netstdRef);

                // Add type-forwards (ExportedTypes)
                foreach (var type in typesToForward)
                {
                    var exportedType = new ExportedType(type.Namespace, type.Name,
                        newModule, netstdRef)
                    { Attributes = TypeAttributes.Forwarder };
                    newModule.ExportedTypes.Add(exportedType);
                }

                var outputPath = Path.Combine(forwardDir, Path.GetFileName(refPath));
                newAsm.Write(outputPath);
                newAsm.Dispose();

                Console.WriteLine($"  {fileName}: {typesToForward.Count} type-forwards → netstandard");
                totalRedirected += typesToForward.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARN: {fileName}: {ex.Message}");
            }
        }

        Console.WriteLine($"Total: {totalRedirected} type-forwards created in {forwardDir}/");

        // Also create a forwarding System.Drawing.Common.dll that forwards to System.Drawing
        // (AL code declares types under assembly("System.Drawing.Common") but the merged
        // assembly is System.Drawing-merged.dll deployed as System.Drawing.dll)
        CreateDrawingCommonForwarder(forwardDir);
    }

    static void CreateDrawingCommonForwarder(string outputDir)
    {
        var drawingMergedPath = Path.Combine(PatchedDir, "System.Drawing-merged.dll");
        if (!File.Exists(drawingMergedPath)) return;

        // Find the original System.Drawing.Common.dll to get its assembly identity
        var drawingCommonPaths = new[]
        {
            Path.Combine(ServiceTierDir, "System.Drawing.Common.dll"),
            Path.Combine(RefAsmDir, "System.Drawing.Common.dll"),
        };
        string? srcPath = drawingCommonPaths.FirstOrDefault(File.Exists);
        if (srcPath == null)
        {
            Console.WriteLine("  WARN: System.Drawing.Common.dll not found, skipping forwarder");
            return;
        }

        using var srcAsm = AssemblyDefinition.ReadAssembly(srcPath, new ReaderParameters { ReadSymbols = false });
        using var drawingAsm = AssemblyDefinition.ReadAssembly(drawingMergedPath, new ReaderParameters { ReadSymbols = false });

        // Collect types from the merged System.Drawing assembly
        var drawingTypes = new HashSet<string>();
        CollectTypeNames(drawingAsm.MainModule.Types, drawingTypes);

        // Create forwarding assembly
        var newAsm = AssemblyDefinition.CreateAssembly(srcAsm.Name, srcAsm.Name.Name, ModuleKind.Dll);
        var module = newAsm.MainModule;

        // Add System.Drawing assembly reference (the merged one)
        var drawingRef = new AssemblyNameReference("System.Drawing", drawingAsm.Name.Version)
        {
            PublicKeyToken = drawingAsm.Name.PublicKeyToken
        };
        module.AssemblyReferences.Add(drawingRef);

        // Also add netstandard ref for types from there
        var mergedPath = Path.Combine(PatchedDir, "netstandard-merged.dll");
        using var netstdAsm = AssemblyDefinition.ReadAssembly(mergedPath, new ReaderParameters { ReadSymbols = false });
        var netstdTypes = new HashSet<string>();
        CollectTypeNames(netstdAsm.MainModule.Types, netstdTypes);

        var netstdRef = new AssemblyNameReference("netstandard", netstdAsm.Name.Version)
        {
            PublicKeyToken = netstdAsm.Name.PublicKeyToken
        };
        module.AssemblyReferences.Add(netstdRef);

        int count = 0;
        foreach (var type in srcAsm.MainModule.Types)
        {
            if (type.Name == "<Module>") continue;

            // Determine target: prefer System.Drawing-merged, fallback to netstandard
            IMetadataScope targetRef;
            if (drawingTypes.Contains(type.FullName))
                targetRef = drawingRef;
            else if (netstdTypes.Contains(type.FullName))
                targetRef = netstdRef;
            else
                continue;

            var exported = new ExportedType(type.Namespace, type.Name, module, targetRef)
            { Attributes = TypeAttributes.Forwarder };
            module.ExportedTypes.Add(exported);
            count++;
        }

        // Also forward types that are ExportedTypes in the source
        foreach (var et in srcAsm.MainModule.ExportedTypes)
        {
            if (et.DeclaringType != null) continue;
            var fullName = string.IsNullOrEmpty(et.Namespace) ? et.Name : et.Namespace + "." + et.Name;

            IMetadataScope targetRef;
            if (drawingTypes.Contains(fullName))
                targetRef = drawingRef;
            else if (netstdTypes.Contains(fullName))
                targetRef = netstdRef;
            else
                continue;

            var exported = new ExportedType(et.Namespace, et.Name, module, targetRef)
            { Attributes = TypeAttributes.Forwarder };
            module.ExportedTypes.Add(exported);
            count++;
        }

        var outPath = Path.Combine(outputDir, "System.Drawing.Common.dll");
        newAsm.Write(outPath);
        newAsm.Dispose();
        Console.WriteLine($"  System.Drawing.Common: {count} type-forwards → System.Drawing/netstandard");
    }

    static void CollectTypeNames(IEnumerable<TypeDefinition> types, HashSet<string> names)
    {
        foreach (var type in types)
        {
            if (type.Name == "<Module>") continue;
            names.Add(type.FullName);
        }
    }

    static void MergeAssembly(string sourcePath, string outputPath, string[]? extraSearchDirs)
    {
        Console.WriteLine($"Reading {sourcePath}");
        if (!File.Exists(sourcePath))
        {
            Console.WriteLine($"  ERROR: Source assembly not found: {sourcePath}");
            return;
        }

        // Build the full search path for this assembly
        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        var allSearchDirs = new List<string> { sourceDir };
        if (extraSearchDirs != null)
        {
            foreach (var d in extraSearchDirs)
                if (!allSearchDirs.Contains(d))
                    allSearchDirs.Add(d);
        }
        foreach (var d in SearchDirs)
            if (!allSearchDirs.Contains(d))
                allSearchDirs.Add(d);

        // Set up a resolver so Cecil can find referenced assemblies during Write
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in allSearchDirs)
        {
            if (Directory.Exists(dir))
                resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters
        {
            ReadWrite = false,
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, readerParams);
        var module = asm.MainModule;

        Console.WriteLine($"Assembly: {asm.Name.FullName}");
        Console.WriteLine($"ExportedTypes (type-forwards): {module.ExportedTypes.Count}");
        Console.WriteLine($"Existing types: {module.Types.Count}");

        // Collect ALL exported types, including nested ones
        // Group top-level forwards by target assembly
        var forwardsByAssembly = new Dictionary<string, List<ExportedType>>();
        var nestedForwards = new List<ExportedType>();

        foreach (var et in module.ExportedTypes)
        {
            if (et.DeclaringType != null)
            {
                // This is a nested type forward (e.g., Dictionary`2+KeyCollection)
                nestedForwards.Add(et);
                continue;
            }

            var scope = et.Scope;
            string asmName;
            if (scope is AssemblyNameReference anr)
                asmName = anr.Name;
            else
                continue;

            if (!forwardsByAssembly.ContainsKey(asmName))
                forwardsByAssembly[asmName] = new List<ExportedType>();
            forwardsByAssembly[asmName].Add(et);
        }

        Console.WriteLine($"\nTarget assemblies: {forwardsByAssembly.Count}");
        foreach (var kvp in forwardsByAssembly.OrderByDescending(k => k.Value.Count))
            Console.WriteLine($"  {kvp.Key}: {kvp.Value.Count} types");
        if (nestedForwards.Count > 0)
            Console.WriteLine($"Nested type-forwards: {nestedForwards.Count}");

        // Cache loaded assemblies
        var asmCache = new Dictionary<string, AssemblyDefinition>();

        int copied = 0, failed = 0, skipped = 0;
        var failedTypes = new List<string>();

        // For each target assembly, find the types and create stubs
        foreach (var (asmName, forwards) in forwardsByAssembly)
        {
            var dllPath = FindAssembly(asmName, allSearchDirs);
            if (dllPath == null)
            {
                Console.WriteLine($"  WARNING: {asmName}.dll not found in any search dir, skipping {forwards.Count} types");
                skipped += forwards.Count;
                continue;
            }

            if (!asmCache.TryGetValue(asmName, out var targetAsm))
            {
                try
                {
                    targetAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadSymbols = false });
                    asmCache[asmName] = targetAsm;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to read {dllPath}: {ex.Message}");
                    skipped += forwards.Count;
                    continue;
                }
            }

            foreach (var fwd in forwards)
            {
                var fullName = string.IsNullOrEmpty(fwd.Namespace)
                    ? fwd.Name
                    : fwd.Namespace + "." + fwd.Name;

                // Find the type in the target assembly
                var srcType = targetAsm.MainModule.Types.FirstOrDefault(t =>
                    t.Name == fwd.Name && t.Namespace == fwd.Namespace);

                if (srcType == null)
                {
                    // Type might be forwarded further - just create an empty stub
                    try
                    {
                        CreateEmptyStub(module, fwd.Namespace, fwd.Name, null);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failedTypes.Add($"{fullName} (stub failed: {ex.Message})");
                    }
                    continue;
                }

                try
                {
                    CopyTypeStub(module, srcType, null);
                    copied++;
                }
                catch (Exception ex)
                {
                    // Fallback: create empty stub
                    try
                    {
                        CreateEmptyStub(module, fwd.Namespace, fwd.Name, srcType);
                        copied++;
                    }
                    catch
                    {
                        failed++;
                        failedTypes.Add($"{fullName}: {ex.Message}");
                    }
                }
            }
        }

        // Now handle nested type-forwards (e.g., Dictionary`2+KeyCollection)
        // These reference a declaring ExportedType which we've already resolved above
        foreach (var nested in nestedForwards)
        {
            var fullName = BuildNestedFullName(nested);

            try
            {
                ResolveNestedForward(module, nested, asmCache, allSearchDirs);
                copied++;
            }
            catch (Exception ex)
            {
                // Create a minimal nested stub as fallback
                try
                {
                    EnsureNestedStub(module, nested);
                    copied++;
                }
                catch
                {
                    failed++;
                    failedTypes.Add($"{fullName} (nested): {ex.Message}");
                }
            }
        }

        // Remove all ExportedTypes (type-forwards)
        module.ExportedTypes.Clear();

        Console.WriteLine($"\nResults: {copied} copied, {failed} failed, {skipped} skipped");
        if (failedTypes.Count > 0)
        {
            Console.WriteLine("Failed types:");
            foreach (var ft in failedTypes.Take(20))
                Console.WriteLine($"  {ft}");
            if (failedTypes.Count > 20)
                Console.WriteLine($"  ... and {failedTypes.Count - 20} more");
        }

        Console.WriteLine($"Total types in module now: {module.Types.Count}");

        // Write output
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        asm.Write(outputPath);
        Console.WriteLine($"\nWritten to {outputPath}");
        Console.WriteLine($"Size: {new FileInfo(outputPath).Length:N0} bytes");

        // Cleanup
        foreach (var a in asmCache.Values)
            a.Dispose();
    }

    /// <summary>
    /// Find an assembly DLL in the search directories.
    /// </summary>
    static string? FindAssembly(string asmName, List<string> searchDirs)
    {
        foreach (var dir in searchDirs)
        {
            var path = Path.Combine(dir, asmName + ".dll");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Build the full name for a nested ExportedType (e.g., "System.Collections.Generic.Dictionary`2+KeyCollection").
    /// </summary>
    static string BuildNestedFullName(ExportedType et)
    {
        if (et.DeclaringType != null)
        {
            var parentName = BuildNestedFullName(et.DeclaringType);
            return parentName + "+" + et.Name;
        }
        return string.IsNullOrEmpty(et.Namespace) ? et.Name : et.Namespace + "." + et.Name;
    }

    /// <summary>
    /// Resolve a nested type-forward by finding its declaring type in the module
    /// and ensuring the nested type exists within it (copying from source if needed).
    /// </summary>
    static void ResolveNestedForward(ModuleDefinition module, ExportedType nested,
        Dictionary<string, AssemblyDefinition> asmCache, List<string> searchDirs)
    {
        // Walk up the chain to find the top-level declaring type and its assembly
        var chain = new List<ExportedType>();
        var current = nested;
        while (current != null)
        {
            chain.Insert(0, current);
            current = current.DeclaringType;
        }

        // chain[0] is the top-level type, chain[1..] are the nested types
        var topLevel = chain[0];

        // Find the declaring type in the module (should have been copied already)
        var parentType = module.Types.FirstOrDefault(t =>
            t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);

        if (parentType == null)
        {
            // The top-level type wasn't copied - try to find and copy it
            string? targetAsmName = null;
            if (topLevel.Scope is AssemblyNameReference anr)
                targetAsmName = anr.Name;

            if (targetAsmName != null)
            {
                var dllPath = FindAssembly(targetAsmName, searchDirs);
                if (dllPath != null)
                {
                    if (!asmCache.TryGetValue(targetAsmName, out var targetAsm))
                    {
                        targetAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadSymbols = false });
                        asmCache[targetAsmName] = targetAsm;
                    }
                    var srcType = targetAsm.MainModule.Types.FirstOrDefault(t =>
                        t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);
                    if (srcType != null)
                    {
                        parentType = CopyTypeStub(module, srcType, null);
                    }
                }
            }

            if (parentType == null)
            {
                // Create empty stub for parent
                parentType = CreateEmptyStub(module, topLevel.Namespace, topLevel.Name, null);
            }
        }

        // Now walk down the chain to ensure each nested type exists
        var currentParent = parentType;
        for (int i = 1; i < chain.Count; i++)
        {
            var nestedEt = chain[i];
            var existingNested = currentParent.NestedTypes.FirstOrDefault(t => t.Name == nestedEt.Name);

            if (existingNested != null)
            {
                currentParent = existingNested;
                continue;
            }

            // Try to find the nested type in the source assembly
            // Walk through the top-level scope to find the assembly
            string? srcAsmName = null;
            if (topLevel.Scope is AssemblyNameReference anr2)
                srcAsmName = anr2.Name;

            TypeDefinition? srcNested = null;
            if (srcAsmName != null && asmCache.TryGetValue(srcAsmName, out var srcAsm))
            {
                // Navigate to the corresponding nested type in source
                var srcParent = srcAsm.MainModule.Types.FirstOrDefault(t =>
                    t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);
                if (srcParent != null)
                {
                    for (int j = 1; j <= i; j++)
                    {
                        var step = chain[j];
                        var found = srcParent.NestedTypes.FirstOrDefault(t => t.Name == step.Name);
                        if (found == null) break;
                        if (j == i)
                            srcNested = found;
                        else
                            srcParent = found;
                    }
                }
            }

            if (srcNested != null)
            {
                currentParent = CopyTypeStub(module, srcNested, currentParent);
            }
            else
            {
                // Create empty nested stub
                var nestedType = new TypeDefinition("", nestedEt.Name,
                    TypeAttributes.NestedPublic | TypeAttributes.Class,
                    module.TypeSystem.Object);
                currentParent.NestedTypes.Add(nestedType);
                currentParent = nestedType;
            }
        }
    }

    /// <summary>
    /// Ensure a minimal nested type stub exists for the given ExportedType chain.
    /// Used as fallback when full resolution fails.
    /// </summary>
    static void EnsureNestedStub(ModuleDefinition module, ExportedType nested)
    {
        var chain = new List<ExportedType>();
        var current = nested;
        while (current != null)
        {
            chain.Insert(0, current);
            current = current.DeclaringType;
        }

        var topLevel = chain[0];
        var parentType = module.Types.FirstOrDefault(t =>
            t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);

        if (parentType == null)
        {
            parentType = CreateEmptyStub(module, topLevel.Namespace, topLevel.Name, null);
        }

        var currentParent = parentType;
        for (int i = 1; i < chain.Count; i++)
        {
            var nestedEt = chain[i];
            var existing = currentParent.NestedTypes.FirstOrDefault(t => t.Name == nestedEt.Name);
            if (existing != null)
            {
                currentParent = existing;
                continue;
            }

            var nestedType = new TypeDefinition("", nestedEt.Name,
                TypeAttributes.NestedPublic | TypeAttributes.Class,
                module.TypeSystem.Object);
            currentParent.NestedTypes.Add(nestedType);
            currentParent = nestedType;
        }
    }

    // =========================================================================
    // Type remapping: ensures all type references point to local TypeDefinitions
    // in the merged assembly (not cross-assembly references), and generic
    // parameters are correctly remapped to the target type/method.
    // =========================================================================

    /// <summary>
    /// Find a TypeDefinition in the target module by namespace+name, including nested types.
    /// </summary>
    static TypeDefinition? FindLocalType(ModuleDefinition module, TypeReference typeRef)
    {
        if (typeRef is TypeDefinition td && td.Module == module)
            return td;

        // Handle nested types
        if (typeRef.DeclaringType != null)
        {
            var parent = FindLocalType(module, typeRef.DeclaringType);
            if (parent != null)
                return parent.NestedTypes.FirstOrDefault(t => t.Name == typeRef.Name);
            return null;
        }

        return module.Types.FirstOrDefault(t =>
            t.Name == typeRef.Name && t.Namespace == typeRef.Namespace);
    }

    /// <summary>
    /// Remap a type reference to use local TypeDefinitions and correct generic parameters.
    /// This is the core fix: instead of ImportReference (which creates cross-assembly refs),
    /// we resolve to local types in the merged assembly wherever possible.
    /// </summary>
    static TypeReference RemapType(TypeReference typeRef, ModuleDefinition targetModule,
        IGenericParameterProvider? targetTypeProvider, IGenericParameterProvider? targetMethodProvider)
    {
        if (typeRef == null)
            return targetModule.TypeSystem.Object;

        // Generic parameter: remap to target's generic parameter by position
        if (typeRef is GenericParameter gp)
        {
            if (gp.Type == GenericParameterType.Type && targetTypeProvider != null
                && gp.Position < targetTypeProvider.GenericParameters.Count)
                return targetTypeProvider.GenericParameters[gp.Position];
            if (gp.Type == GenericParameterType.Method && targetMethodProvider != null
                && gp.Position < targetMethodProvider.GenericParameters.Count)
                return targetMethodProvider.GenericParameters[gp.Position];
            // Fallback: try to import
            return targetModule.ImportReference(typeRef);
        }

        // Generic instance: remap element type and all generic arguments
        if (typeRef is GenericInstanceType git)
        {
            var elementType = RemapType(git.ElementType, targetModule, targetTypeProvider, targetMethodProvider);
            var result = new GenericInstanceType(elementType);
            foreach (var arg in git.GenericArguments)
                result.GenericArguments.Add(RemapType(arg, targetModule, targetTypeProvider, targetMethodProvider));
            return result;
        }

        // Array type
        if (typeRef is ArrayType at)
        {
            var elementType = RemapType(at.ElementType, targetModule, targetTypeProvider, targetMethodProvider);
            return new ArrayType(elementType, at.Rank);
        }

        // ByReference type
        if (typeRef is ByReferenceType brt)
        {
            var elementType = RemapType(brt.ElementType, targetModule, targetTypeProvider, targetMethodProvider);
            return new ByReferenceType(elementType);
        }

        // Pointer type
        if (typeRef is PointerType pt)
        {
            var elementType = RemapType(pt.ElementType, targetModule, targetTypeProvider, targetMethodProvider);
            return new PointerType(elementType);
        }

        // Regular type: check if it exists locally in the target module
        var local = FindLocalType(targetModule, typeRef);
        if (local != null)
            return local;

        // Not found locally — use ImportReference
        return targetModule.ImportReference(typeRef);
    }

    /// <summary>
    /// Copy a type definition as a stub into the target module.
    /// Methods get empty/throw bodies; fields, properties, events are copied.
    /// All type references are remapped to use local TypeDefinitions.
    /// </summary>
    static TypeDefinition CopyTypeStub(ModuleDefinition targetModule, TypeDefinition srcType, TypeDefinition? declaringType)
    {
        // Check if type already exists
        if (declaringType == null)
        {
            var existing = targetModule.Types.FirstOrDefault(t =>
                t.Name == srcType.Name && t.Namespace == srcType.Namespace);
            if (existing != null)
                return existing;
        }
        else
        {
            var existing = declaringType.NestedTypes.FirstOrDefault(t => t.Name == srcType.Name);
            if (existing != null)
                return existing;
        }

        var newType = new TypeDefinition(
            declaringType == null ? srcType.Namespace : "",
            srcType.Name,
            srcType.Attributes);

        // Generic parameters (must be added BEFORE remapping other types)
        CopyGenericParameters(srcType, newType, targetModule);

        // Base type (remap to local if possible)
        if (srcType.BaseType != null)
        {
            try { newType.BaseType = RemapType(srcType.BaseType, targetModule, newType, null); }
            catch { newType.BaseType = targetModule.TypeSystem.Object; }
        }

        // Interfaces
        foreach (var iface in srcType.Interfaces)
        {
            try
            {
                newType.Interfaces.Add(new InterfaceImplementation(
                    RemapType(iface.InterfaceType, targetModule, newType, null)));
            }
            catch { /* skip problematic interfaces */ }
        }

        // Add to module EARLY so nested types and later lookups can find this type
        if (declaringType == null)
            targetModule.Types.Add(newType);
        else
            declaringType.NestedTypes.Add(newType);

        // Fields (public only for non-enum, all for enum)
        foreach (var field in srcType.Fields)
        {
            if (!field.IsPublic && !field.IsFamily && !field.IsFamilyOrAssembly && !srcType.IsEnum)
                continue;

            try
            {
                var newField = new FieldDefinition(field.Name, field.Attributes,
                    RemapType(field.FieldType, targetModule, newType, null));
                if (field.HasConstant)
                    newField.Constant = field.Constant;
                if (field.InitialValue != null && field.InitialValue.Length > 0)
                    newField.InitialValue = field.InitialValue;
                newType.Fields.Add(newField);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARN: field {srcType.FullName}.{field.Name}: {ex.Message}");
            }
        }

        // Methods (public/protected only)
        foreach (var method in srcType.Methods)
        {
            if (!method.IsPublic && !method.IsFamily && !method.IsFamilyOrAssembly)
                continue;

            try
            {
                var newMethod = new MethodDefinition(method.Name, method.Attributes,
                    targetModule.TypeSystem.Void); // placeholder, set after generic params

                CopyGenericParameters(method, newMethod, targetModule);

                // Now set return type with proper remapping (method generic params available)
                newMethod.ReturnType = RemapType(method.ReturnType, targetModule, newType, newMethod);

                foreach (var param in method.Parameters)
                {
                    var newParam = new ParameterDefinition(param.Name, param.Attributes,
                        RemapType(param.ParameterType, targetModule, newType, newMethod));
                    if (param.HasConstant)
                        newParam.Constant = param.Constant;
                    newMethod.Parameters.Add(newParam);
                }

                // Stub body: return default (for non-abstract, non-extern methods)
                // Use return-default instead of throw-null so stubs don't crash at runtime
                // (Add-Ins is a runtime probing path too, e.g. ImageFormatConverter during schema sync)
                if (method.HasBody)
                {
                    newMethod.Body = new Mono.Cecil.Cil.MethodBody(newMethod);
                    var il = newMethod.Body.GetILProcessor();
                    var retType = newMethod.ReturnType;
                    if (retType.FullName == "System.Void")
                    {
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    }
                    else if (retType.IsValueType || retType is GenericParameter)
                    {
                        // For value types: create local, load default, return
                        var local = new Mono.Cecil.Cil.VariableDefinition(retType);
                        newMethod.Body.Variables.Add(local);
                        newMethod.Body.InitLocals = true;
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloca_S, local));
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Initobj, retType));
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    }
                    else
                    {
                        // For reference types: return null
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    }
                }

                newType.Methods.Add(newMethod);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARN: method {srcType.FullName}.{method.Name}: {ex.Message}");
            }
        }

        // Properties (public only)
        foreach (var prop in srcType.Properties)
        {
            var getter = prop.GetMethod;
            var setter = prop.SetMethod;
            bool isPublic = (getter != null && (getter.IsPublic || getter.IsFamily || getter.IsFamilyOrAssembly))
                         || (setter != null && (setter.IsPublic || setter.IsFamily || setter.IsFamilyOrAssembly));
            if (!isPublic) continue;

            try
            {
                var newProp = new PropertyDefinition(prop.Name, prop.Attributes,
                    RemapType(prop.PropertyType, targetModule, newType, null));
                if (prop.HasConstant)
                    newProp.Constant = prop.Constant;

                // Link to the copied methods (match by name + parameter count + param types)
                if (getter != null)
                    newProp.GetMethod = FindMatchingMethod(newType, getter);
                if (setter != null)
                    newProp.SetMethod = FindMatchingMethod(newType, setter);

                newType.Properties.Add(newProp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARN: prop {srcType.FullName}.{prop.Name}: {ex.Message}");
            }
        }

        // Events (public only)
        foreach (var evt in srcType.Events)
        {
            var add = evt.AddMethod;
            var remove = evt.RemoveMethod;
            bool isPublic = (add != null && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly))
                         || (remove != null && (remove.IsPublic || remove.IsFamily || remove.IsFamilyOrAssembly));
            if (!isPublic) continue;

            try
            {
                var newEvt = new EventDefinition(evt.Name, evt.Attributes,
                    RemapType(evt.EventType, targetModule, newType, null));
                if (add != null)
                    newEvt.AddMethod = newType.Methods.FirstOrDefault(m => m.Name == add.Name);
                if (remove != null)
                    newEvt.RemoveMethod = newType.Methods.FirstOrDefault(m => m.Name == remove.Name);
                newType.Events.Add(newEvt);
            }
            catch { /* skip problematic events */ }
        }

        // Custom attributes on the type (skip — they're not needed for compilation)

        // Nested types (recursive) - copy ALL public/protected nested types
        foreach (var nested in srcType.NestedTypes)
        {
            if (!nested.IsNestedPublic && !nested.IsNestedFamily && !nested.IsNestedFamilyOrAssembly)
                continue;
            try
            {
                CopyTypeStub(targetModule, nested, newType);
            }
            catch { /* skip problematic nested types */ }
        }

        return newType;
    }

    /// <summary>
    /// Create a minimal empty type stub when we can't find/copy the real type.
    /// </summary>
    static TypeDefinition CreateEmptyStub(ModuleDefinition targetModule, string ns, string name, TypeDefinition? srcType)
    {
        // Check if already exists
        var existing = targetModule.Types.FirstOrDefault(t => t.Name == name && t.Namespace == ns);
        if (existing != null) return existing;

        var attrs = TypeAttributes.Public;
        TypeReference? baseType = targetModule.TypeSystem.Object;

        if (srcType != null)
        {
            attrs = srcType.Attributes;
            if (srcType.BaseType != null)
            {
                try { baseType = RemapType(srcType.BaseType, targetModule, null, null); }
                catch { }
            }
            if (srcType.IsInterface)
                baseType = null;
        }

        var newType = new TypeDefinition(ns, name, attrs, baseType);

        if (srcType != null)
            CopyGenericParameters(srcType, newType, targetModule);

        targetModule.Types.Add(newType);
        return newType;
    }

    /// <summary>
    /// Find a method in the target type that matches the source method by name,
    /// parameter count, AND parameter type names. Needed for indexer overloads
    /// like DataRow.Item[string] vs DataRow.Item[DataColumn].
    /// </summary>
    static MethodDefinition? FindMatchingMethod(TypeDefinition targetType, MethodDefinition srcMethod)
    {
        // First try exact match by name + param count + param type names
        foreach (var m in targetType.Methods)
        {
            if (m.Name != srcMethod.Name || m.Parameters.Count != srcMethod.Parameters.Count)
                continue;
            bool match = true;
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                if (m.Parameters[i].ParameterType.FullName != srcMethod.Parameters[i].ParameterType.FullName)
                {
                    match = false;
                    break;
                }
            }
            if (match) return m;
        }
        // Fallback: match by name + param count only
        return targetType.Methods.FirstOrDefault(m =>
            m.Name == srcMethod.Name && m.Parameters.Count == srcMethod.Parameters.Count);
    }

    static void CopyGenericParameters(IGenericParameterProvider source, IGenericParameterProvider target, ModuleDefinition module)
    {
        foreach (var gp in source.GenericParameters)
        {
            var newGp = new GenericParameter(gp.Name, target)
            {
                Attributes = gp.Attributes
            };

            foreach (var constraint in gp.Constraints)
            {
                try
                {
                    newGp.Constraints.Add(new GenericParameterConstraint(
                        RemapType(constraint.ConstraintType, module, target, null)));
                }
                catch { /* skip problematic constraints */ }
            }

            target.GenericParameters.Add(newGp);
        }
    }
}
