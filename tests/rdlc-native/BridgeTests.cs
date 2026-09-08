using System;
using System.Linq;
using System.Reflection;

internal static class BridgeTests
{
    private static int Main()
    {
        var visual = new int[4];
        var logical = new int[4];
        Bridge.Dispatch("ScriptLayout", null, new object[]
        {
            4, new byte[] {1, 2, 2, 1}, visual, logical
        });
        Require(visual.SequenceEqual(new[] {3, 1, 2, 0}), "L2 must preserve an embedded LTR pair");
        for (int i = 0; i < visual.Length; i++)
            Require(logical[visual[i]] == i, "Bidi ordering must be invertible");

        var assembly = Assembly.LoadFrom("run/Microsoft.ReportViewer.Common.dll");
        var native = assembly.GetType("Microsoft.ReportingServices.Rendering.RichText.Win32", true);
        native.GetMethod("ScriptLayout", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] {4, new byte[] {1, 2, 2, 1}, visual, logical});
        Require(visual.SequenceEqual(new[] {3, 1, 2, 0}), "Patched original method must reach the bridge");
        var timer = assembly.GetType("Microsoft.ReportingServices.Diagnostics.Timer", true);
        var ticks = new object[] {0L};
        Require((bool)timer.GetMethod("QueryPerformanceCounter", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, ticks), "Patched performance counter");
        Require((long)ticks[0] > 0, "Patched by-ref output must propagate");
        var type = assembly.GetType("Microsoft.ReportingServices.Rendering.RichText.SCRIPT_ANALYSIS", true);
        object ltr = Activator.CreateInstance(type);
        object rtl = Activator.CreateInstance(type);
        type.GetField("word1", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SetValue(rtl, (ushort)0x400);
        var args = new object[] {1, false, 3, 3, new short[] {0, 1, 2}, null,
            new[] {10, 20, 30}, ltr, 0};
        Bridge.Dispatch("ScriptCPtoX", null, args);
        Require((int)args[8] == 10, "LTR leading edge");
        args = new object[] {0, false, 3, 3, new short[] {2, 1, 0}, null,
            new[] {30, 20, 10}, rtl, 0};
        Bridge.Dispatch("ScriptCPtoX", null, args);
        Require((int)args[8] == 60, "RTL leading edge");
        args[1] = true;
        Bridge.Dispatch("ScriptCPtoX", null, args);
        Require((int)args[8] == 50, "RTL trailing edge");
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("public report fixture");
        foreach (int flags in new[] {1, 5})
        {
            byte[] cipher = (byte[])Bridge.Dispatch("ProtectData", null, new object[] {plaintext, flags});
            Require(!plaintext.SequenceEqual(cipher), "Protection must not return plaintext");
            byte[] restored = (byte[])Bridge.Dispatch("UnprotectData", null, new object[] {cipher, 1});
            Require(plaintext.SequenceEqual(restored), "Data protection round trip");
            cipher[cipher.Length - 1] ^= 1;
            bool refused = false;
            try { Bridge.Dispatch("UnprotectData", null, new object[] {cipher, 1}); }
            catch (System.Security.Cryptography.CryptographicException) { refused = true; }
            Require(refused, "Modified protected data must be refused");
        }
        Console.WriteLine("Managed bidi ordering and logical-cluster boundaries passed.");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
