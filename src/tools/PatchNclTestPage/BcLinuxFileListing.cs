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
///  * the result is sorted by full path, ignoring case, which is the order NTFS keeps its folder
///    entries in. Recursive listings are sorted the same way, which is not the exact
///    traversal order of a Windows listing but is stable.
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
        // for the case of the pattern.
        EnumerationOptions options = new EnumerationOptions();
        options.MatchType = MatchType.Win32;
        options.MatchCasing = MatchCasing.CaseInsensitive;
        options.AttributesToSkip = (FileAttributes)0;
        options.IgnoreInaccessible = false;
        options.RecurseSubdirectories = searchOption == SearchOption.AllDirectories;

        Type[] signature = isDirectory
            ? new Type[] { typeof(string), typeof(string), typeof(EnumerationOptions) }
            : new Type[] { typeof(string), typeof(EnumerationOptions) };
        MethodInfo target = declaring.GetMethod(name, signature);
        if (target == null)
            return method.Invoke(instance, args);

        object[] targetArgs = isDirectory
            ? new object[] { args[0], pattern, options }
            : new object[] { pattern, options };
        object result = target.Invoke(instance, targetArgs);
        if (result == null)
            return null;

        // Collect the elements. A lazy result is enumerated here, so an error such as a missing
        // folder is raised from the same call as for the eager methods.
        Array array = result as Array;
        object[] items;
        if (array != null)
        {
            items = new object[array.Length];
            for (int i = 0; i < items.Length; i++)
                items[i] = array.GetValue(i);
        }
        else
        {
            IEnumerable sequence = result as IEnumerable;
            if (sequence == null)
                return result;
            ArrayList collected = new ArrayList();
            IEnumerator walker = sequence.GetEnumerator();
            while (walker.MoveNext())
                collected.Add(walker.Current);
            items = collected.ToArray();
        }

        string[] keys = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            string text = items[i] as string;
            FileSystemInfo entry = items[i] as FileSystemInfo;
            keys[i] = text != null ? text : (entry != null ? entry.FullName : "");
        }
        Array.Sort(keys, items, StringComparer.OrdinalIgnoreCase);

        Type returnType = info.ReturnType;
        Type elementType = returnType.IsArray ? returnType.GetElementType() : returnType.GetGenericArguments()[0];
        Array sorted = Array.CreateInstance(elementType, items.Length);
        for (int i = 0; i < items.Length; i++)
            sorted.SetValue(items[i], i);
        return sorted;
    }
}
