using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

// No reference to ReportViewer or System.Drawing is needed at compile time.
public static class FontBridge
{
    private const string Library = "rdlc_native";
    private const string RichText = "Microsoft.ReportingServices.Rendering.RichText.";
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public |
        BindingFlags.NonPublic;

    [StructLayout(LayoutKind.Sequential)]
    public struct Metrics
    {
        public uint Size, UnitsPerEm, GlyphCount, Weight;
        public uint FsType, Os2Version, Italic, Charset;
        public uint FirstChar, LastChar, DefaultChar, BreakChar;
        public uint PitchFlags, Decorations, Reserved1, Reserved2;
        public double LogicalEm, Dpi;
        public double CellAscent, CellDescent, InternalLeading, ExternalLeading;
        public double TypoAscent, TypoDescent, LineGap;
        public double XMin, YMin, XMax, YMax, ItalicAngle;
        public double AverageWidth, MaximumWidth;
        public double UnderlinePosition, UnderlineThickness;
        public double StrikeoutPosition, StrikeoutThickness;
    }

    private static class Native
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rdlc_font_last_error();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string family, int bold, int italic,
            int charset, double em, double dpi, out IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_create_file(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint index, int bold,
            int italic, int charset, double em, double dpi, out IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_resize(IntPtr source, double em, out IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_set_decorations(IntPtr token, int underline, int strikeout);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_destroy(IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_is_token(IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_select(IntPtr hdc, IntPtr token, out IntPtr previous);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_selected(IntPtr hdc, out IntPtr token);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_forget_hdc(IntPtr hdc);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_get_metrics(IntPtr token, ref Metrics metrics);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_get_winansi_widths(
            IntPtr token, uint first, uint last, [Out] float[] abc);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_can_embed(IntPtr token, out int allowed);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_package(IntPtr token, [In] ushort[] glyphs,
            uint count, out IntPtr bytes, out uint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rdlc_font_free_buffer(IntPtr bytes);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_acquire_hb_font(IntPtr token, out IntPtr font);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rdlc_font_release_hb_font(IntPtr font);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rdlc_font_describe(IntPtr token, [Out] byte[] buffer,
            UIntPtr capacity);
    }

    public static object Dispatch(string method, object instance, object[] args)
    {
        if (args == null) throw new ArgumentNullException("args");
        try
        {
            switch (NormalizeMethod(method))
            {
                case "FontCache.CreateGdiFont":
                    Require(args, 8);
                    return CreateLayoutFont(instance, args);
                case "CachedFont.Initialize":
                    Require(args, 2);
                    InitializeCachedFont(instance, args[0], args[1]);
                    return null;
                case "Win32.SelectObject.Safe":
                    Require(args, 2);
                    return NewFontHandle(Select(args[0], args[1]), false);
                case "Win32.SelectObject.Raw":
                    Require(args, 2);
                    return Select(args[0], args[1]);
                case "Win32.DeleteObject":
                    Require(args, 1);
                    Check(Native.rdlc_font_destroy(Handle(args[0])));
                    return 1;
                case "GraphicsBase.ReleaseHdc":
                    Require(args, 0);
                    ReleaseGraphicsHdc(instance);
                    return null;
                case "FontPackage.CheckEmbeddingRights":
                    Require(args, 1);
                    return CanEmbed(GetSelectedFontToken(args[0]));
                case "FontPackage.CheckSimulatedFontStyles":
                    Require(args, 4);
                    CheckStyles(args);
                    return null;
                case "FontPackage.Generate":
                    Require(args, 3);
                    return PackageFont(GetSelectedFontToken(args[0]), (ushort[])args[2]);
                case "Win32.GetOutlineTextMetrics":
                    Require(args, 3);
                    if (Convert.ToUInt32(args[1], CultureInfo.InvariantCulture) == 0)
                        throw Failure("Outline metric size-only query is not supported");
                    args[2] = OutlineMetrics(GetSelectedFontToken(args[0]),
                        Convert.ToUInt32(args[1], CultureInfo.InvariantCulture));
                    return Convert.ToUInt32(args[1], CultureInfo.InvariantCulture);
                case "Win32.GetCharABCWidthsFloat":
                    Require(args, 4);
                    FillWidths(args);
                    return 1;
                case "Win32.GetTextMetrics":
                    Require(args, 2);
                    args[1] = TextMetrics(GetMetrics(GetSelectedFontToken(args[0])));
                    return true;
                default:
                    throw Failure("Unknown font dispatch target: " + method);
            }
        }
        catch (TargetInvocationException exception)
        {
            if (exception.InnerException == null) throw;
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        catch (DllNotFoundException exception)
        {
            throw Failure("Cannot load native font provider: " + exception.Message);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw Failure("Native font provider ABI mismatch: " + exception.Message);
        }
    }

    // The caller owns this token. Wrap it with NewFontHandle(token, true) if it
    // is installed into a CachedFont. Never use Font.ToHfont() as this token.
    public static IntPtr CreateFace(string path, uint index, bool bold, bool italic,
        byte charset, double logicalEm, double dpi)
    {
        IntPtr token;
        Check(Native.rdlc_font_create_file(path, index, bold ? 1 : 0, italic ? 1 : 0,
            charset, logicalEm, dpi, out token));
        return token;
    }

    public static void ReleaseFontToken(IntPtr token)
    {
        Check(Native.rdlc_font_destroy(token));
    }

    public static bool IsFontToken(IntPtr token)
    {
        return Native.rdlc_font_is_token(token) != 0;
    }

    public static IntPtr GetFontToken(object cachedFont)
    {
        if (cachedFont == null) throw new ArgumentNullException("cachedFont");
        IntPtr token = Handle(Read(cachedFont, "m_hfont"));
        GetMetrics(token); // Validate provider ownership before exposing a token.
        return token;
    }

    public static IntPtr GetSelectedFontToken(object hdc)
    {
        IntPtr token;
        Check(Native.rdlc_font_selected(Handle(hdc), out token));
        return token;
    }

    public static IntPtr GetSelectedFont(object hdc)
    {
        return GetSelectedFontToken(hdc);
    }

    public static Metrics GetMetrics(IntPtr token)
    {
        Metrics metrics = new Metrics();
        metrics.Size = (uint)Marshal.SizeOf(typeof(Metrics));
        Check(Native.rdlc_font_get_metrics(token, ref metrics));
        return metrics;
    }

    public static IntPtr AcquireHarfBuzzFont(IntPtr token)
    {
        IntPtr font;
        Check(Native.rdlc_font_acquire_hb_font(token, out font));
        return font;
    }

    public static void ReleaseHarfBuzzFont(IntPtr font)
    {
        Native.rdlc_font_release_hb_font(font);
    }

    public static string DescribeFont(IntPtr token)
    {
        byte[] buffer = new byte[8192];
        Check(Native.rdlc_font_describe(token, buffer, new UIntPtr((uint)buffer.Length)));
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) throw Failure("Native font description was not terminated");
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    // Alternative to the ToHfont-only interception: replace the whole
    // Font/ToHfont/SafeHandle expression and cast the returned SafeHandle.
    public static object CreatePdfFontHandle(object pdfFont)
    {
        return OwnFontHandle(CreatePdfFontToken(pdfFont));
    }

    // Returns an owned token, transferred to the original SafeHandle constructor.
    public static IntPtr CreatePdfFontToken(object pdfFont)
    {
        object cached = Read(pdfFont, "CachedFont");
        IntPtr source = GetFontToken(cached);
        Metrics metrics = GetMetrics(source);
        int emHeight = Convert.ToInt32(Read(pdfFont, "EMHeight"), CultureInfo.InvariantCulture);
        if (emHeight != metrics.UnitsPerEm)
            throw Failure("PDF font EM height differs from the bound native face");
        IntPtr token;
        Check(Native.rdlc_font_resize(source, emHeight, out token));
        return token;
    }

    public static IntPtr PdfFontToHfont(object drawingFont, object pdfFont)
    {
        IDisposable temporary = drawingFont as IDisposable;
        if (temporary == null) throw Failure("PDF ToHfont interceptor expects a temporary disposable Font");
        if (Object.ReferenceEquals(drawingFont, Read(Read(pdfFont, "CachedFont"), "m_font")))
            throw Failure("PDF ToHfont interceptor must not dispose the CachedFont's Font");
        temporary.Dispose();
        return CreatePdfFontToken(pdfFont);
    }

    // The original PDF writer incorrectly scales the GDI angle as font units.
    // Replace that one scalar expression, not the general outline metric value.
    public static int GetPdfItalicAngle(object pdfFont)
    {
        Metrics metrics = GetMetrics(GetFontToken(Read(pdfFont, "CachedFont")));
        return checked((int)Math.Round(metrics.ItalicAngle));
    }

    public static object NewFontHandle(IntPtr token, bool ownsHandle)
    {
        return Activator.CreateInstance(RvType(RichText + "Win32ObjectSafeHandle"),
            AllInstance, null, new object[] { token, ownsHandle }, CultureInfo.InvariantCulture);
    }

    private static object OwnFontHandle(IntPtr token, bool strikeout = false)
    {
        bool transferred = false;
        try
        {
            if (strikeout) Check(Native.rdlc_font_set_decorations(token, 0, 1));
            object handle = NewFontHandle(token, true);
            transferred = true;
            return handle;
        }
        finally
        {
            if (!transferred) Check(Native.rdlc_font_destroy(token));
        }
    }

    private static object CreateLayoutFont(object cache, object[] args)
    {
        if (cache == null) throw new ArgumentNullException("cache");
        if (!String.Equals(args[0].ToString(), "Horizontal", StringComparison.Ordinal) ||
            Convert.ToBoolean(args[6], CultureInfo.InvariantCulture))
            throw Failure("Vertical and rotated native fonts are not implemented");
        int em = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
        bool bold = Convert.ToBoolean(args[2], CultureInfo.InvariantCulture);
        bool italic = Convert.ToBoolean(args[3], CultureInfo.InvariantCulture);
        int charset = Convert.ToInt32(args[5], CultureInfo.InvariantCulture);
        double dpi = Convert.ToDouble(Read(cache, "m_dpi"), CultureInfo.InvariantCulture);
        IntPtr token;
        Check(Native.rdlc_font_create((string)args[7], bold ? 1 : 0, italic ? 1 : 0,
            charset, em, dpi, out token));
        return OwnFontHandle(token, Convert.ToBoolean(args[4], CultureInfo.InvariantCulture));
    }

    private static void InitializeCachedFont(object cached, object hdc, object cache)
    {
        IntPtr token = GetFontToken(cached);
        Metrics metrics = GetMetrics(token);
        object drawingFont = Read(cached, "m_font");
        object family = Property(drawingFont, "FontFamily");
        int em = Convert.ToInt32(Call(family, "GetEmHeight",
            Property(drawingFont, "Style")), CultureInfo.InvariantCulture);
        if (em != metrics.UnitsPerEm)
            throw Failure("System.Drawing and the provider resolved different EM heights");

        Call(cache, "SelectFontObject", hdc, Read(cached, "m_hfont"));
        object tm = TextMetrics(metrics);
        double scale = Convert.ToDouble(Read(cached, "m_scaleFactor"), CultureInfo.InvariantCulture);
        if (Double.IsNaN(scale) || Double.IsInfinity(scale) || scale <= 0)
            throw Failure("Invalid CachedFont scale factor");
        if (scale != 1.0)
        {
            foreach (string name in new[] { "tmHeight", "tmAscent", "tmDescent", "tminternalLeading" })
            {
                int value = Convert.ToInt32(Read(tm, name), CultureInfo.InvariantCulture);
                Set(tm, name, checked((int)((float)value / (float)scale + 0.5f)));
            }
        }
        Set(cached, "m_textMetric", tm);
        Set(cached, "m_initialized", true);
    }

    private static IntPtr Select(object hdc, object font)
    {
        IntPtr previous;
        Check(Native.rdlc_font_select(Handle(hdc), Handle(font), out previous));
        return previous;
    }

    private static void ReleaseGraphicsHdc(object instance)
    {
        object handle = Read(instance, "m_hdc");
        SafeHandle safe = handle as SafeHandle;
        if (safe == null) throw Failure("Unexpected GraphicsBase HDC type");
        if (safe.IsInvalid) return; // Matches the original no-acquired-HDC case.
        IntPtr hdc = safe.DangerousGetHandle();
        Call(Read(instance, "m_graphicsBase"), "ReleaseHdc");
        Check(Native.rdlc_font_forget_hdc(hdc));
        PropertyInfo zero = handle.GetType().GetProperty("Zero",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (zero == null) throw Failure("HDC SafeHandle has no Zero property");
        Set(instance, "m_hdc", zero.GetValue(null, null));
    }

    private static bool CanEmbed(IntPtr token)
    {
        int allowed;
        Check(Native.rdlc_font_can_embed(token, out allowed));
        if (allowed == 0)
            Console.Error.WriteLine("[FontBridge] Font embedding prohibited: " + DescribeFont(token));
        return allowed != 0;
    }

    private static void CheckStyles(object[] args)
    {
        Metrics metrics = GetMetrics(GetSelectedFontToken(args[0]));
        int weight = Convert.ToInt32(Read(args[1], "tmWeight"), CultureInfo.InvariantCulture);
        bool italic = Convert.ToByte(Read(args[1], "tmItalic"), CultureInfo.InvariantCulture) != 0;
        if ((weight >= 600) != (metrics.Weight >= 600) || italic != (metrics.Italic != 0))
            throw Failure("Requested font metrics imply unsupported synthetic style");
        args[2] = false;
        args[3] = false;
    }

    public static byte[] PackageFont(IntPtr token, ushort[] glyphs)
    {
        if (glyphs == null) throw new ArgumentNullException("glyphs");
        IntPtr bytes;
        uint length;
        Check(Native.rdlc_font_package(token, glyphs, checked((uint)glyphs.Length),
            out bytes, out length));
        try
        {
            if (bytes == IntPtr.Zero || length == 0 || length > Int32.MaxValue)
                throw Failure("Invalid font package returned by native provider");
            byte[] result = new byte[(int)length];
            Marshal.Copy(bytes, result, 0, result.Length);
            return result;
        }
        finally { Native.rdlc_font_free_buffer(bytes); }
    }

    private static object TextMetrics(Metrics metrics)
    {
        object result = Activator.CreateInstance(RvType(RichText + "Win32+TEXTMETRIC"));
        FillTextMetrics(result, metrics, false);
        return result;
    }

    private static void FillTextMetrics(object tm, Metrics m, bool outline)
    {
        Set(tm, "tmAscent", PositiveRound(m.CellAscent));
        Set(tm, "tmDescent", PositiveRound(m.CellDescent));
        Set(tm, "tmHeight", PositiveRound(m.CellAscent) + PositiveRound(m.CellDescent));
        Set(tm, outline ? "tmInternalLeading" : "tminternalLeading", PositiveRound(m.InternalLeading));
        Set(tm, "tmExternalLeading", PositiveRound(m.ExternalLeading));
        Set(tm, "tmAveCharWidth", PositiveRound(m.AverageWidth));
        Set(tm, "tmMaxCharWidth", PositiveRound(m.MaximumWidth));
        Set(tm, "tmWeight", checked((int)m.Weight));
        Set(tm, "tmDigitizedAspectX", PositiveRound(m.Dpi));
        Set(tm, "tmDigitizedAspectY", PositiveRound(m.Dpi));
        Set(tm, "tmItalic", (byte)m.Italic);
        Set(tm, "tmUnderlined", (byte)(m.Decorations & 1));
        Set(tm, "tmStruckOut", (byte)((m.Decorations >> 1) & 1));
        Set(tm, "tmFirstChar", checked((char)m.FirstChar));
        Set(tm, "tmLastChar", checked((char)m.LastChar));
        Set(tm, "tmDefaultChar", checked((char)m.DefaultChar));
        Set(tm, "tmBreakChar", checked((char)m.BreakChar));
        Set(tm, "tmPitchAndFamily", (byte)m.PitchFlags);
        Set(tm, "tmCharSet", (byte)m.Charset);
    }

    private static object OutlineMetrics(IntPtr token, uint requestedSize)
    {
        Metrics m = GetMetrics(token);
        object result = Activator.CreateInstance(RvType(RichText + "Win32+OutlineTextMetric"));
        FillTextMetrics(result, m, true);
        Set(result, "otmSize", requestedSize);
        Set(result, "otmEMSquare", m.UnitsPerEm);
        Set(result, "otmfsType", m.FsType);
        Set(result, "otmfsSelection", (uint)((m.Italic != 0 ? 1 : 0) | (m.Weight >= 600 ? 32 : 0)));
        Set(result, "otmAscent", checked((int)Math.Round(m.TypoAscent)));
        Set(result, "otmDescent", checked((int)Math.Round(m.TypoDescent)));
        Set(result, "left", checked((int)Math.Round(m.XMin)));
        Set(result, "bottom", checked((int)Math.Round(m.YMin)));
        Set(result, "right", checked((int)Math.Round(m.XMax)));
        Set(result, "top", checked((int)Math.Round(m.YMax)));
        Set(result, "otmItalicAngle", checked((int)Math.Round(m.ItalicAngle * 10)));
        Set(result, "otmsUnderscorePosition", checked((int)Math.Round(m.UnderlinePosition)));
        Set(result, "otmsUnderscoreSize", PositiveRound(m.UnderlineThickness));
        Set(result, "otmsStrikeoutPosition", checked((int)Math.Round(m.StrikeoutPosition)));
        Set(result, "otmsStrikeoutSize", checked((uint)PositiveRound(m.StrikeoutThickness)));
        return result;
    }

    private static void FillWidths(object[] args)
    {
        uint first = Convert.ToUInt32(args[1], CultureInfo.InvariantCulture);
        uint last = Convert.ToUInt32(args[2], CultureInfo.InvariantCulture);
        Array result = args[3] as Array;
        if (first > last || last > 255 || result == null || result.Length < last - first + 1)
            throw Failure("Invalid ABCFloat output array/range");
        int count = checked((int)(last - first + 1));
        float[] abc = new float[count * 3];
        Check(Native.rdlc_font_get_winansi_widths(GetSelectedFontToken(args[0]), first, last, abc));
        Type type = result.GetType().GetElementType();
        for (int i = 0; i < count; i++)
        {
            object item = Activator.CreateInstance(type);
            Set(item, "abcfA", abc[i * 3]);
            Set(item, "abcfB", abc[i * 3 + 1]);
            Set(item, "abcfC", abc[i * 3 + 2]);
            result.SetValue(item, i);
        }
    }

    private static int PositiveRound(double value)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0)
            throw Failure("Expected a nonnegative finite font metric");
        return checked((int)Math.Floor(value + 0.5));
    }

    private static IntPtr Handle(object value)
    {
        if (value is IntPtr) return (IntPtr)value;
        SafeHandle handle = value as SafeHandle;
        if (handle == null) throw Failure("Expected IntPtr or SafeHandle");
        if (handle.IsClosed) throw Failure("Font/DC SafeHandle is already closed");
        return handle.DangerousGetHandle();
    }

    private static Assembly ReportViewer()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name == "Microsoft.ReportViewer.Common") return assembly;
        throw new InvalidOperationException("Original Microsoft.ReportViewer.Common is not loaded");
    }

    private static Type RvType(string name)
    {
        return ReportViewer().GetType(name, true);
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (Type current = type; current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, AllInstance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw Failure("Missing field " + type.FullName + "." + name);
    }

    private static object Read(object instance, string name)
    {
        if (instance == null) throw Failure("Null instance reading " + name);
        return Field(instance.GetType(), name).GetValue(instance);
    }

    private static void Set(object instance, string name, object value)
    {
        Field(instance.GetType(), name).SetValue(instance, value);
    }

    private static object Property(object instance, string name)
    {
        PropertyInfo property = instance.GetType().GetProperty(name, AllInstance);
        if (property == null) throw Failure("Missing property " + name);
        return property.GetValue(instance, null);
    }

    private static object Call(object instance, string name, params object[] args)
    {
        MethodInfo found = null;
        foreach (MethodInfo method in instance.GetType().GetMethods(AllInstance))
        {
            if (method.Name != name) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            bool matches = true;
            for (int i = 0; i < args.Length; i++)
                if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]))
                    matches = false;
            if (!matches) continue;
            if (found != null) throw Failure("Ambiguous reflection method " + name);
            found = method;
        }
        if (found == null) throw Failure("Missing reflection method " + name);
        return found.Invoke(instance, args);
    }

    private static string NormalizeMethod(string method)
    {
        if (method == null) throw new ArgumentNullException("method");
        if (method.StartsWith("font:", StringComparison.Ordinal))
            method = method.Substring(5);
        if (method.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            method = method.Substring(2);
        uint token;
        if (method.Length != 8 || !UInt32.TryParse(method, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out token))
            return method;
        switch (token)
        {
            case 0x060066D0: return "FontCache.CreateGdiFont";
            case 0x060066B9: return "CachedFont.Initialize";
            case 0x06006855: return "Win32.SelectObject.Safe";
            case 0x06006854: return "Win32.SelectObject.Raw";
            case 0x0600685B:
            case 0x0600685C: return "Win32.DeleteObject";
            case 0x06006A31: return "GraphicsBase.ReleaseHdc";
            case 0x06006A64: return "FontPackage.CheckSimulatedFontStyles";
            case 0x06006A65: return "FontPackage.CheckEmbeddingRights";
            case 0x06006A66: return "FontPackage.Generate";
            case 0x0600687A:
            case 0x0600687B: return "Win32.GetOutlineTextMetrics";
            case 0x06006874:
            case 0x06006875: return "Win32.GetCharABCWidthsFloat";
            case 0x06006870:
            case 0x06006871: return "Win32.GetTextMetrics";
            default: throw Failure("Unknown original font method token: " + method);
        }
    }

    private static void Require(object[] args, int count)
    {
        if (args.Length != count) throw Failure("Incorrect font dispatcher argument count");
    }

    private static void Check(int status)
    {
        if (status == 0) return;
        IntPtr message = Native.rdlc_font_last_error();
        throw Failure(Marshal.PtrToStringAnsi(message) ?? "Native font provider failed");
    }

    private static Exception Failure(string message)
    {
        string text = "[FontBridge] " + message;
        Console.Error.WriteLine(text);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "Microsoft.ReportViewer.Common") continue;
            Type type = assembly.GetType(
                "Microsoft.ReportingServices.OnDemandReportRendering.ReportRenderingException", false);
            if (type != null)
            {
                ConstructorInfo constructor = type.GetConstructor(AllInstance, null,
                    new Type[] { typeof(string) }, null);
                if (constructor != null)
                    return (Exception)constructor.Invoke(new object[] { text });
            }
        }
        return new InvalidOperationException(text);
    }
}
