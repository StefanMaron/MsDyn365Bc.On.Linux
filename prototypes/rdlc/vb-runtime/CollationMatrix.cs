using System;
using System.Globalization;
using System.Reflection;

public static class CollationMatrix
{
    public static int Main(string[] args)
    {
        bool oracle = args.Length == 1 && args[0] == "--oracle";
        Type adapter = oracle ? null : Assembly.LoadFrom(args[0]).GetType(
            "Microsoft.VisualBasic.CompilerServices.NativeIcuCompareInfo", true);
        var locales = new[] { "en-US", "tr-TR", "az-Latn-AZ", "de-DE", "fr-FR", "sv-SE", "ja-JP", "zh-CHS", "zh-CHT" };
        var pairs = new[,] {
            { "I", "\u0131" }, { "\u0130", "i" }, { "I", "i" }, { "\u0130", "\u0131" },
            { "\u00e9", "e" }, { "\u00e9", "E" }, { "\u00e9", "e\u0301" },
            { "\u00c5", "A" }, { "\u00e4", "z" }, { "\u00df", "ss" },
            { "\u03a3", "\u03c2" }, { "\u03c3", "\u03c2" },
            { "\uff21", "A" }, { "\uff21", "a" }, { "\uff76", "\u30ab" },
            { "\u304b", "\u30ab" }, { "\u304c", "\u30ab" },
            { "\u30ac", "\u30ab\u3099" }, { "\uff76\uff9e", "\u30ac" },
            { "", "" }, { "", "\u00ad" }, { "a\0b", "ab" },
            { "\ud83d\ude00", "\ud83d\ude01" }, { "a\n", "a" },
            { "\u963f", "\u516b" }, { "\u4e00", "\u4e8c" }, { "\u4e2d", "\u56fd" }
        };
        var options = new[] {
            CompareOptions.None, CompareOptions.IgnoreCase, CompareOptions.IgnoreWidth,
            CompareOptions.IgnoreKanaType,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreNonSpace,
            CompareOptions.Ordinal
        };
        foreach (string locale in locales)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(locale);
            object comparer = oracle ? null : adapter.GetMethod("ForCulture").Invoke(null, new object[] { culture });
            foreach (CompareOptions option in options)
            {
                for (int pair = 0; pair < pairs.GetLength(0); pair++)
                {
                    string left = pairs[pair, 0], right = pairs[pair, 1];
                    int comparison = oracle ? culture.CompareInfo.Compare(left, right, option) :
                        (int)adapter.GetMethod("Compare").Invoke(comparer, new object[] { left, right, option });
                    string source = "x" + left + "y" + left + "z";
                    int index = oracle ? culture.CompareInfo.LastIndexOf(source, right, option) :
                        (int)adapter.GetMethod("LastIndexOf").Invoke(comparer, new object[] { source, right, option });
                    Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}", locale, (int)option, pair, Math.Sign(comparison), index);
                }
            }
        }
        return 0;
    }
}
