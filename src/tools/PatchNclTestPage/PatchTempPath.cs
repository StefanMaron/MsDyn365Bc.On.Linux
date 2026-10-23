using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// NavFile.GetUserTempFolder: the folder AL's TemporaryPath() returns (issues #175, #176, #181).
///
/// BC builds it as Path.Combine(userFolder, "TEMP\"). On Windows that is "...\TEMP\". On Linux
/// Path.Combine keeps the backslash as an ordinary character, so TemporaryPath() was
/// "/user/folder/TEMP\" and BC created a real directory literally named "TEMP\". AL's File
/// functions normalize '\' to '/' (Patch #28), so a file written to TemporaryPath() + 'sub\a.xml'
/// landed in "TEMP/sub". Anything that reaches System.IO with the raw string (a DotNet
/// DirectoryInfo or Directory.GetFiles, the MediaSet file name taken with Path.GetFileName)
/// looked in "TEMP\sub" instead and found nothing, or kept "TEMP\" as part of the file name.
///
/// The fix is the constant: "TEMP/" combines to "/user/folder/TEMP/", which AL and System.IO
/// resolve to the same directory. Only that one string changes, so no branch or length moves.
/// Idempotent: once patched there is no "TEMP\" left to find.
/// </summary>
static class PatchTempPath
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

            int rewritten = 0;
            var navFile = assembly.MainModule.GetTypes().FirstOrDefault(t => t.Name == "NavFile");
            var method = navFile?.Methods.FirstOrDefault(m => m.Name == "GetUserTempFolder" && m.HasBody);
            if (method != null)
                foreach (var ins in method.Body.Instructions)
                    if (ins.OpCode.Code == Code.Ldstr && ins.Operand is string s && s == "TEMP\\")
                    {
                        ins.Operand = "TEMP/";
                        rewritten++;
                    }

            Console.WriteLine($"Rewrote {rewritten} TEMP\\ in NavFile.GetUserTempFolder of {Path.GetFileName(inputPath)}");
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
