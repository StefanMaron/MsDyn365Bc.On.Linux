using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

public static class Bridge
{
    public static IntPtr PdfFontToHfont(object drawingFont, object pdfFont)
    {
        return (IntPtr)InvokeFont("PdfFontToHfont", drawingFont, pdfFont);
    }

    public static int PdfItalicAngle(object pdfFont)
    {
        return (int)InvokeFont("GetPdfItalicAngle", pdfFont);
    }

    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;
    private const int Pending = unchecked((int)0x8000000A);
    private const int NoMemory = unchecked((int)0x8007000E);
    private const int FontMissing = unchecked((int)0x80040200);
    private sealed class DeviceState
    {
        internal uint Alignment;
        internal int BackgroundMode = 2;
    }
    private static readonly ConcurrentDictionary<IntPtr, DeviceState> Devices =
        new ConcurrentDictionary<IntPtr, DeviceState>();
    private static readonly Lazy<IntPtr> ScriptProperties = new Lazy<IntPtr>(() =>
    {
        IntPtr value = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(value, (1L << 17) | (1L << 20) | (1L << 34));
        IntPtr pointers = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(pointers, value);
        return pointers;
    });

    private sealed class ShapeCache
    {
        internal IntPtr Font;
    }

    [DllImport("rdlc_native", CharSet = CharSet.Unicode)]
    private static extern int rdlc_itemize(string text, int length, int rtl,
        int capacity, [Out] int[] starts, [Out] int[] levels);
    [DllImport("rdlc_native", CharSet = CharSet.Unicode)]
    private static extern int rdlc_break(string text, int length, int rtl, [Out] byte[] attrs);
    [DllImport("rdlc_native", CharSet = CharSet.Unicode)]
    private static extern int rdlc_shape(IntPtr font, string text, int length, int rtl,
        int capacity, [Out] ushort[] glyphs, [Out] uint[] clusters,
        [Out] int[] advances, [Out] int[] x, [Out] int[] y, [Out] int[] abc);
    [DllImport("rdlc_native")]
    private static extern uint rdlc_nominal_glyph(IntPtr font, uint codepoint);
    [DllImport("rdlc_native")]
    private static extern int rdlc_glyph_advance(IntPtr font, uint glyph);

    public static object Dispatch(string method, object instance, object[] args)
    {
        if (Environment.GetEnvironmentVariable("RDLC_TRACE") == "1")
            Console.Error.WriteLine("Native bridge: " + method);
        if (method.StartsWith("drawing:", StringComparison.Ordinal))
            return DrawingBridge.Dispatch(method.Substring(8), args);
        if (method.StartsWith("font:", StringComparison.Ordinal))
        {
            if (method == "font:06006A31")
            {
                IntPtr hdc = Handle(Get(instance, "m_hdc"));
                object result = InvokeFont("Dispatch", method.Substring(5), instance, args);
                DeviceState ignored;
                Devices.TryRemove(hdc, out ignored);
                return result;
            }
            return InvokeFont("Dispatch", method.Substring(5), instance, args);
        }
        switch (method)
        {
            // PDFWriter emits text itself; RichText still saves/restores these DC settings.
            case "SetTextAlign":
                var alignment = Devices.GetOrAdd(Handle(args[0]), _ => new DeviceState());
                lock (alignment)
                {
                    uint previous = alignment.Alignment;
                    alignment.Alignment = Convert.ToUInt32(args[1]);
                    return previous;
                }
            case "SetBkMode":
                var background = Devices.GetOrAdd(Handle(args[0]), _ => new DeviceState());
                lock (background)
                {
                    int previous = background.BackgroundMode;
                    int next = (int)args[1];
                    if (next != 1 && next != 2) throw new ArgumentOutOfRangeException("background mode");
                    background.BackgroundMode = next;
                    return previous;
                }
            case "QueryPerformanceCounter": args[0] = Stopwatch.GetTimestamp(); return true;
            case "QueryPerformanceFrequency": args[0] = Stopwatch.Frequency; return true;
            case "ProtectData":
                byte scope = ((int)args[1] & 4) != 0 ? (byte)1 : (byte)0;
                byte[] cipher = ProtectedData.Protect((byte[])args[0], null,
                    scope == 1 ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
                var protectedBytes = new byte[cipher.Length + 5];
                protectedBytes[0] = 0x52;
                protectedBytes[1] = 0x44;
                protectedBytes[2] = 0x4c;
                protectedBytes[3] = 1;
                protectedBytes[4] = scope;
                Array.Copy(cipher, 0, protectedBytes, 5, cipher.Length);
                return protectedBytes;
            case "UnprotectData":
                byte[] input = (byte[])args[0];
                if (input.Length <= 5 || input[0] != 0x52 || input[1] != 0x44 ||
                    input[2] != 0x4c || input[3] != 1 || input[4] > 1)
                    throw new CryptographicException("Unsupported report-cache protection format.");
                return ProtectedData.Unprotect(input.Skip(5).ToArray(), null,
                    input[4] == 1 ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
            case "ScriptGetProperties": args[0] = ScriptProperties.Value; args[1] = 1; return 0;
            case "ScriptItemize": return Itemize(args);
            case "ScriptBreak": return Break(args);
            case "ScriptShape": return Shape(args);
            case "ScriptPlaceRun": return Place(args);
            case "ScriptPlace":
                throw new NotSupportedException("Glyph placement requires its original text-run context.");
            case "ScriptFreeCache":
                IntPtr pointer = (IntPtr)args[0];
                if (pointer != IntPtr.Zero) GCHandle.FromIntPtr(pointer).Free();
                args[0] = IntPtr.Zero;
                return 0;
            case "ScriptIsComplex":
                string text = (string)args[0];
                uint options = Convert.ToUInt32(args[2]);
                return text.Take((int)args[1]).Any(c =>
                    ((options & 1) != 0 && c > 0x7f) ||
                    ((options & 2) != 0 && c >= '0' && c <= '9') ||
                    ((options & 4) != 0 && !char.IsLetterOrDigit(c))) ? 0 : 1;
            case "ScriptLayout": return Layout(args);
            case "ScriptCPtoX":
                args[8] = CharacterX((int)args[0], (bool)args[1], (int)args[2],
                    (int)args[3], (short[])args[4], (int[])args[6], IsRtl(args[7]));
                return 0;
            case "ScriptXtoCP":
                return XToCharacter(args);
            case "ScriptGetLogicalWidths":
                int chars = (int)args[1], glyphs = (int)args[2];
                var widths = (int[])args[6];
                for (int c = 0; c < chars; c++)
                    widths[c] = Math.Abs(CharacterX(c, true, chars, glyphs,
                        (short[])args[4], (int[])args[3], IsRtl(args[0])) -
                        CharacterX(c, false, chars, glyphs,
                        (short[])args[4], (int[])args[3], IsRtl(args[0])));
                return 0;
            case "ScriptGetFontProperties":
                IntPtr font = SelectedFont(args[0]);
                if (font == IntPtr.Zero) return Pending;
                object props = args[2];
                Set(props, "wgBlank", rdlc_nominal_glyph(font, 32));
                Set(props, "wgDefault", 0);
                Set(props, "wgInvalid", 0);
                uint kashida = rdlc_nominal_glyph(font, 0x640);
                Set(props, "wgKashida", kashida);
                Set(props, "iKashidaWidth", rdlc_glyph_advance(font, kashida));
                args[2] = props;
                return 0;
            default: throw new NotSupportedException("Unimplemented native rendering call: " + method);
        }
    }

    private static object InvokeFont(string method, params object[] args)
    {
        Type type = typeof(Bridge).Assembly.GetTypes().Single(t => t.Name == "FontBridge");
        try { return type.GetMethod(method, Flags).Invoke(null, args); }
        catch (TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static IntPtr SelectedFont(object hdc)
    {
        if (Handle(hdc) == IntPtr.Zero) return IntPtr.Zero;
        return (IntPtr)InvokeFont("GetSelectedFont", hdc);
    }

    private static int Itemize(object[] args)
    {
        string text = (string)args[0];
        int length = (int)args[1], capacity = (int)args[2];
        var starts = new int[capacity];
        var levels = new int[capacity];
        int rtl = Convert.ToInt32(Get(args[4], "word1")) & 1;
        int count = rdlc_itemize(text, length, rtl, capacity, starts, levels);
        if (count == -2) return NoMemory;
        if (count < 0) throw new InvalidOperationException("Pango could not itemize report text.");
        var items = (Array)args[5];
        Type itemType = items.GetType().GetElementType();
        for (int i = 0; i <= count; i++)
        {
            if (levels[i] > 31)
                throw new NotSupportedException("The report engine cannot represent this bidi nesting level.");
            object item = Activator.CreateInstance(itemType);
            object analysis = Activator.CreateInstance(itemType.GetField("analysis", Flags).FieldType);
            object state = Activator.CreateInstance(analysis.GetType().GetField("state", Flags).FieldType);
            Set(state, "word1", levels[i] & 31);
            Set(analysis, "word1", (levels[i] & 1) != 0 ? 0xc00 : 0);
            Set(analysis, "state", state);
            Set(item, "analysis", analysis);
            Set(item, "iCharPos", starts[i]);
            items.SetValue(item, i);
        }
        args[6] = count;
        return 0;
    }

    private static int Break(object[] args)
    {
        int length = (int)args[1];
        var values = new byte[length];
        if (rdlc_break((string)args[0], length, IsRtl(args[2]) ? 1 : 0, values) != 0)
            throw new InvalidOperationException("Pango could not determine report text boundaries.");
        var output = (Array)args[3];
        Type type = output.GetType().GetElementType();
        for (int i = 0; i < length; i++)
        {
            object attr = Activator.CreateInstance(type);
            Set(attr, "m_value", values[i]);
            output.SetValue(attr, i);
        }
        return 0;
    }

    private static int Shape(object[] args)
    {
        IntPtr cacheHandle = Handle(args[1]);
        ShapeCache cache = cacheHandle == IntPtr.Zero ? null :
            (ShapeCache)GCHandle.FromIntPtr(cacheHandle).Target;
        IntPtr font = SelectedFont(args[0]);
        if (font == IntPtr.Zero && cache != null) font = cache.Font;
        if (font == IntPtr.Zero) return Pending;
        string text = (string)args[2];
        int length = (int)args[3], capacity = (int)args[4];
        var glyphs = new ushort[capacity];
        var clusters = new uint[capacity];
        var advances = new int[capacity];
        var x = new int[capacity];
        var y = new int[capacity];
        var abc = new int[3];
        int count = rdlc_shape(font, text, length, IsRtl(args[5]) ? 1 : 0,
            capacity, glyphs, clusters, advances, x, y, abc);
        if (count == -2) return NoMemory;
        if (count < 0) throw new InvalidOperationException("HarfBuzz could not shape report text.");
        for (int i = 0; i < count; i++)
        {
            int c = checked((int)clusters[i]);
            if (c < 0 || c >= length) throw new InvalidOperationException("Invalid glyph cluster.");
            if (glyphs[i] == 0 && !char.IsWhiteSpace(text[c]) && !char.IsControl(text[c]))
            {
                Console.Error.WriteLine("[RDLC] Selected font cannot shape U+" +
                    char.ConvertToUtf32(text, c).ToString("X4") + "; requesting another face.");
                return FontMissing;
            }
        }
        var outputGlyphs = (short[])args[6];
        var outputClusters = (short[])args[7];
        var visAttrs = (Array)args[8];
        var logical = new SortedDictionary<int, int>();
        for (int g = 0; g < count; g++)
        {
            outputGlyphs[g] = unchecked((short)glyphs[g]);
            int c = checked((int)clusters[g]);
            if (!logical.ContainsKey(c)) logical.Add(c, g);
        }
        int[] boundaries = logical.Keys.Concat(new[] { length }).ToArray();
        for (int i = 0; i + 1 < boundaries.Length; i++)
            for (int c = boundaries[i]; c < boundaries[i + 1]; c++)
                outputClusters[c] = unchecked((short)checked((ushort)logical[boundaries[i]]));
        Type visType = visAttrs.GetType().GetElementType();
        FieldInfo visField = visType.GetFields(Flags).Single(f => !f.IsStatic);
        for (int g = 0; g < count; g++)
        {
            object value = Activator.CreateInstance(visType);
            visField.SetValue(value, Convert.ChangeType(
                logical[(int)clusters[g]] == g ? 0x10 : 0, visField.FieldType));
            visAttrs.SetValue(value, g);
        }
        if (cache == null)
        {
            cache = new ShapeCache();
            cacheHandle = GCHandle.ToIntPtr(GCHandle.Alloc(cache));
            object handle = args[1];
            if (handle == null)
            {
                Type handleType = AppDomain.CurrentDomain.GetAssemblies()
                    .Single(a => a.GetName().Name == "Microsoft.ReportViewer.Common")
                    .GetType("Microsoft.ReportingServices.Rendering.RichText.ScriptCacheSafeHandle", true);
                handle = Activator.CreateInstance(handleType, true);
            }
            typeof(SafeHandle).GetMethod("SetHandle", Flags).Invoke(handle, new object[] { cacheHandle });
            args[1] = handle;
        }
        cache.Font = font;
        args[9] = count;
        return 0;
    }

    private static int Place(object[] args)
    {
        IntPtr handle = Handle(args[1]);
        var cache = handle == IntPtr.Zero ? null : (ShapeCache)GCHandle.FromIntPtr(handle).Target;
        IntPtr font = SelectedFont(args[0]);
        if (font == IntPtr.Zero && cache != null) font = cache.Font;
        if (font == IntPtr.Zero) return Pending;
        int count = (int)args[3];
        string text = (string)Get(args[9], "m_text");
        var glyphs = new ushort[count];
        var clusters = new uint[count];
        var advances = new int[count];
        var x = new int[count];
        var y = new int[count];
        var metrics = new int[3];
        // Uniscribe caches are per font, while ReportViewer can reuse an earlier run's glyphs.
        // Reposition that run's actual text, not whichever string was shaped most recently.
        int shaped = rdlc_shape(font, text, text.Length, IsRtl(args[5]) ? 1 : 0,
            count, glyphs, clusters, advances, x, y, metrics);
        if (shaped != count || !glyphs.SequenceEqual(((short[])args[2])
            .Take(count).Select(g => unchecked((ushort)g))))
            throw new InvalidOperationException("Cached glyphs do not match the selected face and original text.");
        Array.Copy(advances, (int[])args[6], count);
        var offsets = (Array)args[7];
        Type type = offsets.GetType().GetElementType();
        for (int i = 0; i < count; i++)
        {
            object offset = Activator.CreateInstance(type);
            Set(offset, "du", x[i]);
            Set(offset, "dv", y[i]);
            offsets.SetValue(offset, i);
        }
        object abc = args[8];
        Set(abc, "abcA", metrics[0]);
        Set(abc, "abcB", checked((uint)metrics[1]));
        Set(abc, "abcC", metrics[2]);
        args[8] = abc;
        return 0;
    }

    private static int Layout(object[] args)
    {
        int count = (int)args[0];
        var levels = (byte[])args[1];
        int[] order = Enumerable.Range(0, count).ToArray();
        int max = count == 0 ? 0 : levels.Take(count).Max();
        int minOdd = levels.Take(count).Where(x => (x & 1) != 0).Select(x => (int)x).DefaultIfEmpty(max + 1).Min();
        for (int level = max; level >= minOdd; level--)
        {
            int start = 0;
            while (start < count)
            {
                while (start < count && levels[order[start]] < level) start++;
                int end = start;
                while (end < count && levels[order[end]] >= level) end++;
                Array.Reverse(order, start, end - start);
                start = end;
            }
        }
        if (args[2] != null) Array.Copy(order, (int[])args[2], count);
        if (args[3] != null)
            for (int i = 0; i < count; i++) ((int[])args[3])[order[i]] = i;
        return 0;
    }

    private static int CharacterX(int cp, bool trailing, int chars, int glyphs,
        short[] clusters, int[] advances, bool rtl)
    {
        if (cp < 0 || cp > chars) throw new ArgumentOutOfRangeException("cp");
        int total = advances.Take(glyphs).Sum();
        if (cp == chars) return rtl ? 0 : total;
        int first = cp, end = cp + 1;
        while (first > 0 && clusters[first - 1] == clusters[cp]) first--;
        while (end < chars && clusters[end] == clusters[cp]) end++;
        int glyphStart = (ushort)clusters[cp];
        int glyphEnd = clusters.Take(chars).Select(x => (int)(ushort)x)
            .Where(x => x > glyphStart).DefaultIfEmpty(glyphs).Min();
        int left = advances.Take(glyphStart).Sum();
        int width = advances.Skip(glyphStart).Take(glyphEnd - glyphStart).Sum();
        double fraction = (double)(cp - first + (trailing ? 1 : 0)) / (end - first);
        return left + (int)Math.Round(width * (rtl ? 1 - fraction : fraction));
    }

    private static int XToCharacter(object[] args)
    {
        int x = (int)args[0], chars = (int)args[1], glyphs = (int)args[2];
        var clusters = (short[])args[3];
        var advances = (int[])args[5];
        bool rtl = IsRtl(args[6]);
        int best = 0, distance = int.MaxValue, trailing = 0;
        for (int i = 0; i < chars; i++)
        {
            int a = CharacterX(i, false, chars, glyphs, clusters, advances, rtl);
            int b = CharacterX(i, true, chars, glyphs, clusters, advances, rtl);
            int d = Math.Min(Math.Abs(x - a), Math.Abs(x - b));
            if (d < distance) { distance = d; best = i; trailing = Math.Abs(x - b) < Math.Abs(x - a) ? 1 : 0; }
        }
        args[7] = best;
        args[8] = trailing;
        return 0;
    }

    private static IntPtr Handle(object value)
    {
        if (value == null) return IntPtr.Zero;
        if (value is IntPtr) return (IntPtr)value;
        return ((SafeHandle)value).DangerousGetHandle();
    }
    private static bool IsRtl(object analysis) { return (Convert.ToInt32(Get(analysis, "word1")) & 0x400) != 0; }
    private static object Get(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
        }
        throw new MissingFieldException(target.GetType().FullName, name);
    }
    private static void Set(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, Flags);
        field.SetValue(target, field.FieldType.IsInstanceOfType(value) ? value :
            Convert.ChangeType(value, field.FieldType));
    }
}
