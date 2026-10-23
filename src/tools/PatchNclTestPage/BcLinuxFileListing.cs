using System;
using System.Collections;
using System.IO;
using System.Reflection;

/// <summary>
/// Directory listings the way NTFS gives them (issue #183). Cloned into Nav.Ncl.dll by
/// HelperCloner, so the same rules apply as for BcLinuxCulture: framework calls only, no
/// lambdas, closures, static fields or try/catch (the copy carries over none of them).
///
/// A DotNet call that AL makes reaches NavDotNet.Invoke, which ends in MethodBase.Invoke.
/// That call goes through <see cref="Invoke"/>. For the listing methods of System.IO.Directory
/// and System.IO.DirectoryInfo (GetFiles, GetDirectories, GetFileSystemEntries/Infos and the
/// Enumerate variants):
///  * the pattern is matched case-insensitively, so '*.xml' finds 'b.XML' as it does on Windows;
///  * the entries of a folder come in name order, ignoring case, which is the order NTFS keeps
///    its folder entries in. A recursive listing lists a folder first and then its subfolders,
///    first in, first out, which is how .NET's own enumerator walks a tree.
/// Overloads that take an EnumerationOptions, and every other method, run unchanged.
/// A lazy Enumerate result comes back as an array of the same element type.
/// </summary>
internal static class BcLinuxFileListing
{
    public static object Invoke(MethodBase method, object instance, object[] args)
    {
        MethodInfo info = method as MethodInfo;
        if (info == null || args == null)
            return method.Invoke(instance, args);

        Type declaring = info.DeclaringType;
        bool isDirectory = declaring == typeof(Directory);
        if (!isDirectory && declaring != typeof(DirectoryInfo))
            return method.Invoke(instance, args);

        string name = info.Name;
        bool listing = string.Equals(name, "GetFiles", StringComparison.Ordinal)
            || string.Equals(name, "GetDirectories", StringComparison.Ordinal)
            || string.Equals(name, "GetFileSystemEntries", StringComparison.Ordinal)
            || string.Equals(name, "GetFileSystemInfos", StringComparison.Ordinal)
            || string.Equals(name, "EnumerateFiles", StringComparison.Ordinal)
            || string.Equals(name, "EnumerateDirectories", StringComparison.Ordinal)
            || string.Equals(name, "EnumerateFileSystemEntries", StringComparison.Ordinal)
            || string.Equals(name, "EnumerateFileSystemInfos", StringComparison.Ordinal);
        if (!listing)
            return method.Invoke(instance, args);

        ParameterInfo[] parameters = info.GetParameters();
        string pattern = "*";
        SearchOption searchOption = SearchOption.TopDirectoryOnly;
        int first = isDirectory ? 1 : 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            if (parameterType == typeof(EnumerationOptions))
                return method.Invoke(instance, args);
            if (i >= args.Length || args[i] == null || args[i] is Missing)
                continue;
            if (parameterType == typeof(SearchOption))
                searchOption = (SearchOption)args[i];
            else if (parameterType == typeof(string) && i >= first)
                pattern = (string)args[i];
        }

        // The same lookup rules as the legacy overloads (EnumerationOptions.Compatible), except
        // for the case of the pattern. Each folder is listed on its own (no recursion in the
        // options), so the folder order below is ours to choose.
        EnumerationOptions options = new EnumerationOptions();
        options.MatchType = MatchType.Win32;
        options.MatchCasing = MatchCasing.CaseInsensitive;
        options.AttributesToSkip = (FileAttributes)0;
        options.IgnoreInaccessible = false;
        options.RecurseSubdirectories = false;

        Type[] signature = isDirectory
            ? new Type[] { typeof(string), typeof(string), typeof(EnumerationOptions) }
            : new Type[] { typeof(string), typeof(EnumerationOptions) };
        MethodInfo target = declaring.GetMethod(name, signature);
        MethodInfo subfolders = typeof(Directory).GetMethod("GetDirectories",
            new Type[] { typeof(string), typeof(string), typeof(EnumerationOptions) });
        if (target == null || subfolders == null)
            return method.Invoke(instance, args);

        // The order .NET itself walks a tree with SearchOption.AllDirectories: the entries of a
        // folder first, then its subfolders in a first-in-first-out queue. Within a folder the
        // entries are in NTFS order (by name, ignoring case).
        string root = isDirectory ? (string)args[0] : ((DirectoryInfo)instance).FullName;
        ArrayList folders = new ArrayList();
        folders.Add(root);
        if (searchOption == SearchOption.AllDirectories)
        {
            for (int f = 0; f < folders.Count; f++)
            {
                string[] children = (string[])subfolders.Invoke(null, new object[] { (string)folders[f], "*", options });
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < children.Length; c++)
                    folders.Add(children[c]);
            }
        }

        ArrayList items = new ArrayList();
        for (int f = 0; f < folders.Count; f++)
        {
            string folder = (string)folders[f];
            object[] targetArgs = isDirectory
                ? new object[] { folder, pattern, options }
                : new object[] { pattern, options };
            object folderInstance = isDirectory ? null : (f == 0 ? instance : new DirectoryInfo(folder));
            object result = target.Invoke(folderInstance, targetArgs);
            if (result == null)
                continue;

            // A lazy result is enumerated here, so an error such as a missing folder is raised
            // from the same call as for the eager methods.
            IEnumerable sequence = result as IEnumerable;
            if (sequence == null)
                return result;
            ArrayList inFolder = new ArrayList();
            IEnumerator walker = sequence.GetEnumerator();
            while (walker.MoveNext())
                inFolder.Add(walker.Current);

            object[] entries = inFolder.ToArray();
            string[] keys = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                string text = entries[i] as string;
                FileSystemInfo entry = entries[i] as FileSystemInfo;
                keys[i] = text != null ? text : (entry != null ? entry.FullName : "");
            }
            Array.Sort(keys, entries, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Length; i++)
                items.Add(entries[i]);
        }

        Type returnType = info.ReturnType;
        Type elementType = returnType.IsArray ? returnType.GetElementType() : returnType.GetGenericArguments()[0];
        Array sorted = Array.CreateInstance(elementType, items.Count);
        for (int i = 0; i < items.Count; i++)
            sorted.SetValue(items[i], i);
        return sorted;
    }
}
