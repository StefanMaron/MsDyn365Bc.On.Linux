using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualBasic.CompilerServices;

public static class IcuLikeProbe
{
    static int failures;
    static void Check(string locale, string source, string pattern, bool expected)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(locale);
        bool actual = StringType.StrLikeText(source, pattern);
        Console.WriteLine("{0}: {1}: {2} Like {3}: actual={4}, expected={5}",
            actual == expected ? "PASS" : "FAIL", locale, source, pattern, actual, expected);
        if (actual != expected) failures++;
    }
    public static int Main()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Check("tr-TR", "I", "\u0131", true);
        Check("tr-TR", "\u0130", "i", true);
        Check("tr-TR", "I", "i", false);
        Check("tr-TR", "\u0130", "\u0131", false);
        Check("tr-TR", "xIyI", "*\u0131*", true);
        Check("tr-TR", "x\u0130y\u0130", "*i*", true);
        Check("tr-TR", "xIyI", "*i*", false);
        Check("az-Latn-AZ", "I", "\u0131", true);
        Check("en-US", "caf\u00e9", "cafe", false);
        Check("en-US", "caf\u00e9", "cafe\u0301", true);
        Check("fr-FR", "\u00c9", "\u00e9", true);
        Check("fr-FR", "\u00c9", "e", false);
        Check("ja-JP", "\uff21", "a", true);
        Check("ja-JP", "\uff76", "\u30ab", true);
        Check("ja-JP", "\u304b", "\u30ab", true);
        Check("ja-JP", "\u304c", "\u30ab", false);
        Check("sv-SE", "\u00e5", "[a-z]", false);
        Check("en-US", "\u00e5", "[a-z]", true);
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
