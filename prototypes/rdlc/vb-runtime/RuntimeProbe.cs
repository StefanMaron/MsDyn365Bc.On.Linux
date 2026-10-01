using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

public class RuntimeProbe
{
    static int failures;
    static void Check(string name, object actual, object expected)
    {
        bool ok = Equals(actual, expected);
        Console.WriteLine("{0}: {1}: actual={2}, expected={3}", ok ? "PASS" : "FAIL", name, actual, expected);
        if (!ok) failures++;
    }
    public static int Main()
    {
        Console.WriteLine(typeof(StringType).Assembly.FullName);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Check("star", StringType.StrLikeText("Invoice line 001", "invoice*"), true);
        Check("question", StringType.StrLikeText("cat", "c?t"), true);
        Check("digit", StringType.StrLikeText("line7", "line#"), true);
        Check("not digit", StringType.StrLikeText("lineA", "line#"), false);
        Check("set", StringType.StrLikeText("B", "[a-c]"), true);
        Check("negated set", StringType.StrLikeText("D", "[!a-c]"), true);
        Check("negated set excludes", StringType.StrLikeText("B", "[!a-c]"), false);
        Check("binary case", StringType.StrLikeBinary("B", "b"), false);
        Check("LikeOperator", LikeOperator.LikeString("Invoice 003", "invoice ###", CompareMethod.Text), true);
        Check("multiply decimal", Operators.MultiplyObject(3, 1.25m), 3.75m);
        Check("latebind decimal", NewLateBinding.LateGet(12.5m, null, "ToString", new object[] { "F2", CultureInfo.InvariantCulture }, null, null, null), "12.50");
        Check("Strings Mid", Strings.Mid("abcdef", 2, 3), "bcd");
        Check("Conversions decimal", Conversions.ToDecimal("12.50"), 12.5m);
        Check("Conversions integer", Conversions.ToInteger("42"), 42);
        Check("empty star", StringType.StrLikeText("", "*"), true);
        Check("empty group", StringType.StrLikeText("", "[]"), true);
        Check("literal bracket", StringType.StrLikeText("[", "[[]"), true);
        Check("literal exclamation", StringType.StrLikeText("!", "[!]"), true);
        Check("accented range", StringType.StrLikeText("\u00e9", "[a-z]"), true);
        Check("trailing newline is not ignored", StringType.StrLikeText("a\n", "a"), false);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        Console.WriteLine("Mono CompareInfo Turkish dotted I: " +
            CultureInfo.CurrentCulture.CompareInfo.Compare("\u0130", "i", CompareOptions.IgnoreCase));
        Console.WriteLine("Mono CompareInfo Turkish dotless I: " +
            CultureInfo.CurrentCulture.CompareInfo.Compare("I", "\u0131", CompareOptions.IgnoreCase));
        Check("Turkish dotted I", StringType.StrLikeText("\u0130", "i"), true);
        Check("Turkish dotless I", StringType.StrLikeText("I", "\u0131"), true);
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
