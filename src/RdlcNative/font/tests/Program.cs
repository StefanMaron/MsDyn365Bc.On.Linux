// Contract doubles, not an implementation or substitute for ReportViewer.
// The assembly name lets the dispatcher exercise its reflection-only ABI.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RT = Microsoft.ReportingServices.Rendering.RichText;

internal static class Program
{
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Main()
    {
        RT.FontCache cache = new RT.FontCache();
        object[] create = { RT.WritingModes.Horizontal, 16, false, false, true, (byte)1, false, "sans-serif" };
        RT.Win32ObjectSafeHandle font = (RT.Win32ObjectSafeHandle)
            FontBridge.Dispatch("060066d0", cache, create);
        IntPtr token = font.DangerousGetHandle();
        FontBridge.Metrics metrics = FontBridge.GetMetrics(token);
        Assert(FontBridge.IsFontToken(token), "ownership");
        Console.WriteLine(FontBridge.DescribeFont(token));
        RT.CachedFont cached = new RT.CachedFont(font, metrics.UnitsPerEm, 1);
        RT.Win32DCSafeHandle hdc = new RT.Win32DCSafeHandle(new IntPtr(123), false);
        FontBridge.Dispatch("font:060066B9", cached, new object[] { hdc, cache });
        Assert(cached.Initialized && cached.Metric.tmHeight > 0, "cached metrics initialization");
        Assert(cached.Metric.tmStruckOut == 1, "strikeout metadata");
        Assert(cached.Metric.tmFirstChar <= cached.Metric.tmLastChar, "character range");
        Assert(FontBridge.GetSelectedFontToken(hdc) == token, "HDC selection");
        Assert(FontBridge.GetSelectedFont(hdc) == token, "text bridge HDC API");
        Assert(FontBridge.GetFontToken(cached) == token, "cached font identity");
        object[] tmArgs = { hdc, null };
        Assert((bool)FontBridge.Dispatch("Win32.GetTextMetrics", null, tmArgs), "text metrics return");
        Assert(((RT.Win32.TEXTMETRIC)tmArgs[1]).tmHeight == cached.Metric.tmHeight, "text metrics ref writeback");
        object[] style = { hdc, cached.Metric, true, true };
        FontBridge.Dispatch("FontPackage.CheckSimulatedFontStyles", null, style);
        Assert(!(bool)style[2] && !(bool)style[3], "style ref writeback");
        object[] otm = { hdc, (uint)256, null };
        Assert((uint)FontBridge.Dispatch("Win32.GetOutlineTextMetrics", null, otm) == 256, "outline result");
        Assert(((RT.Win32.OutlineTextMetric)otm[2]).otmEMSquare == metrics.UnitsPerEm, "outline units");
        RT.Win32.ABCFloat[] abc = new RT.Win32.ABCFloat[256];
        FontBridge.Dispatch("Win32.GetCharABCWidthsFloat", null, new object[] { hdc, (uint)0, (uint)255, abc });
        Assert(abc[65].abcfA + abc[65].abcfB + abc[65].abcfC > 0, "ABC output");
        Assert((bool)FontBridge.Dispatch("FontPackage.CheckEmbeddingRights", null, new object[] { hdc }), "embedding");
        byte[] package = (byte[])FontBridge.Dispatch("FontPackage.Generate", null,
            new object[] { hdc, "sans-serif", new ushort[] { 0, 1, 2, 2 } });
        Assert(package.Length > 12 && package[0] == 0 && package[1] == 1, "TrueType package");
        PdfFont pdf = new PdfFont(cached, checked((int)metrics.UnitsPerEm));
        TemporaryFont temporary = new TemporaryFont();
        IntPtr rawPdfToken = FontBridge.PdfFontToHfont(temporary, pdf);
        Assert(temporary.Disposed && FontBridge.IsFontToken(rawPdfToken), "ToHfont-only interception");
        using (RT.Win32ObjectSafeHandle handle = (RT.Win32ObjectSafeHandle)
            FontBridge.NewFontHandle(rawPdfToken, true))
            Assert(FontBridge.GetMetrics(rawPdfToken).LogicalEm == metrics.UnitsPerEm, "raw PDF EM token");
        Assert(!FontBridge.IsFontToken(rawPdfToken), "original SafeHandle owns PDF token");
        using (RT.Win32ObjectSafeHandle emFont = (RT.Win32ObjectSafeHandle)FontBridge.CreatePdfFontHandle(pdf))
        {
            FontBridge.Metrics em = FontBridge.GetMetrics(emFont.DangerousGetHandle());
            Assert(em.LogicalEm == metrics.UnitsPerEm && emFont.DangerousGetHandle() != token, "owned EM font");
            RT.CachedFont emCached = new RT.CachedFont(emFont, metrics.UnitsPerEm,
                (float)metrics.UnitsPerEm / 16);
            FontBridge.Dispatch("CachedFont.Initialize", emCached, new object[] { hdc, cache });
            Assert(Math.Abs(emCached.Metric.tmHeight - cached.Metric.tmHeight) <= 1, "EM scale contract");
        }
        Assert(FontBridge.GetPdfItalicAngle(pdf) == 0, "upright angle");
        FontBridge.Dispatch("Win32.SelectObject.Raw", null, new object[] { new IntPtr(123), token });
        GraphicsBase graphics = new GraphicsBase(hdc);
        FontBridge.Dispatch("GraphicsBase.ReleaseHdc", graphics, new object[0]);
        Assert(graphics.Released, "real HDC release path");
        bool failed = false;
        try { FontBridge.GetSelectedFontToken(hdc); }
        catch (Microsoft.ReportingServices.OnDemandReportRendering.ReportRenderingException) { failed = true; }
        Assert(failed, "native errors become rendering exceptions");
        IntPtr hbFont = FontBridge.AcquireHarfBuzzFont(token);
        Assert(hbFont != IntPtr.Zero, "HB acquire");
        font.Dispose();
        Assert(!FontBridge.IsFontToken(token), "safehandle release");
        FontBridge.ReleaseHarfBuzzFont(hbFont);
        create[4] = false;
        create[3] = true;
        using (RT.Win32ObjectSafeHandle italic = (RT.Win32ObjectSafeHandle)
            FontBridge.Dispatch("FontCache.CreateGdiFont", cache, create))
        {
            FontBridge.Metrics italicMetrics = FontBridge.GetMetrics(italic.DangerousGetHandle());
            RT.CachedFont italicCached = new RT.CachedFont(italic, italicMetrics.UnitsPerEm, 1);
            Assert(FontBridge.GetPdfItalicAngle(new PdfFont(italicCached,
                checked((int)italicMetrics.UnitsPerEm))) < 0, "PDF italic angle in degrees");
        }
        create[0] = RT.WritingModes.Vertical;
        failed = false;
        try { FontBridge.Dispatch("FontCache.CreateGdiFont", cache, create); }
        catch (Microsoft.ReportingServices.OnDemandReportRendering.ReportRenderingException) { failed = true; }
        Assert(failed, "unsupported direction fails explicitly");
        Console.WriteLine("Managed reflection/native ABI assertions passed");
    }

    private sealed class TemporaryFont : IDisposable
    {
        internal bool Disposed;
        public void Dispose() { Disposed = true; }
    }

    private sealed class PdfFont
    {
        internal RT.CachedFont CachedFont;
        internal int EMHeight;
        internal PdfFont(RT.CachedFont font, int em) { CachedFont = font; EMHeight = em; }
    }

    private sealed class GraphicsBase
    {
        private RT.Win32DCSafeHandle m_hdc;
        private readonly Graphics m_graphicsBase = new Graphics();
        internal bool Released { get { return m_graphicsBase.Released; } }
        internal GraphicsBase(RT.Win32DCSafeHandle hdc) { m_hdc = hdc; }
    }

    private sealed class Graphics
    {
        internal bool Released;
        public void ReleaseHdc() { Released = true; }
    }
}

namespace Microsoft.ReportingServices.OnDemandReportRendering
{
    internal sealed class ReportRenderingException : Exception
    {
        internal ReportRenderingException(string message) : base(message) { }
    }
}

namespace Microsoft.ReportingServices.Rendering.RichText
{
    internal enum WritingModes { Horizontal, Vertical, Rotate270 }

    internal sealed class Win32ObjectSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Win32ObjectSafeHandle(IntPtr value, bool owns) : base(owns) { SetHandle(value); }
        protected override bool ReleaseHandle()
        {
            return (int)FontBridge.Dispatch("Win32.DeleteObject", null, new object[] { handle }) == 1;
        }
    }

    internal sealed class Win32DCSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public static Win32DCSafeHandle Zero { get { return new Win32DCSafeHandle(IntPtr.Zero, false); } }
        public Win32DCSafeHandle(IntPtr value, bool owns) : base(owns) { SetHandle(value); }
        protected override bool ReleaseHandle() { throw new NotSupportedException("Test HDC must be borrowed"); }
    }

    internal sealed class FontCache
    {
        private readonly float m_dpi = 96;
        internal void SelectFontObject(Win32DCSafeHandle hdc, Win32ObjectSafeHandle font)
        {
            using (SafeHandle old = (SafeHandle)FontBridge.Dispatch("Win32.SelectObject.Safe",
                null, new object[] { hdc, font })) { }
        }
    }

    internal sealed class CachedFont
    {
        private readonly Win32ObjectSafeHandle m_hfont;
        private readonly DrawingFont m_font;
        private readonly float m_scaleFactor;
        private Win32.TEXTMETRIC m_textMetric;
        private bool m_initialized;
        internal bool Initialized { get { return m_initialized; } }
        internal Win32.TEXTMETRIC Metric { get { return m_textMetric; } }
        internal CachedFont(Win32ObjectSafeHandle handle, uint em, float scale)
        {
            m_hfont = handle;
            m_font = new DrawingFont(em);
            m_scaleFactor = scale;
        }
    }

    internal sealed class DrawingFont
    {
        public Family FontFamily { get; private set; }
        public int Style { get { return 0; } }
        internal DrawingFont(uint em) { FontFamily = new Family(em); }
    }

    internal sealed class Family
    {
        private readonly uint em;
        internal Family(uint emHeight) { em = emHeight; }
        public int GetEmHeight(int style) { return checked((int)em); }
    }

    internal static class Win32
    {
        internal struct TEXTMETRIC
        {
            internal int tmHeight, tmAscent, tmDescent, tminternalLeading, tmExternalLeading;
            internal int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang;
            internal int tmDigitizedAspectX, tmDigitizedAspectY;
            internal char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            internal byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        internal struct ABCFloat { internal float abcfA, abcfB, abcfC; }

        internal struct OutlineTextMetric
        {
            internal uint otmSize;
            internal int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
            internal int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang;
            internal int tmDigitizedAspectX, tmDigitizedAspectY;
            internal char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            internal byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
            internal uint otmfsSelection, otmfsType, otmEMSquare;
            internal int otmAscent, otmDescent, left, top, right, bottom, otmItalicAngle;
            internal int otmsUnderscorePosition, otmsUnderscoreSize, otmsStrikeoutPosition;
            internal uint otmsStrikeoutSize;
        }
    }
}
