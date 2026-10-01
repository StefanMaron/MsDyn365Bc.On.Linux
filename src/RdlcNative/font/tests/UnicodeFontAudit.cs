using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

// Coverage inspection only: does not render or change any font/run binding.
internal static class UnicodeFontAudit
{
    [DllImport("rdlc_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rdlc_font_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string family, int bold, int italic,
        int charset, double em, double dpi, out IntPtr token);

    [DllImport("rdlc_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rdlc_font_create_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint index, int bold, int italic,
        int charset, double em, double dpi, out IntPtr token);

    [DllImport("rdlc_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rdlc_font_get_glyph(IntPtr token, uint scalar, out uint glyph);

    [DllImport("rdlc_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rdlc_font_last_error();

    private static string Error()
    {
        return Marshal.PtrToStringAnsi(rdlc_font_last_error());
    }

    private static void AuditFace(string label, string family, bool bold, bool italic,
        float points, string text)
    {
        IntPtr token;
        int status = Path.IsPathRooted(family)
            ? rdlc_font_create_file(family, 0, bold ? 1 : 0, italic ? 1 : 0, 1,
                points * 96.0 / 72, 96, out token)
            : rdlc_font_create(family, bold ? 1 : 0, italic ? 1 : 0, 1,
                points * 96.0 / 72, 96, out token);
        if (status != 0)
        {
            Console.WriteLine("  " + label + " REJECT: " + Error());
            return;
        }
        try
        {
            SortedSet<uint> missing = new SortedSet<uint>();
            SortedSet<ushort> glyphs = new SortedSet<ushort>();
            int scalarCount = 0;
            for (int offset = 0; offset < text.Length; offset++)
            {
                uint scalar = checked((uint)Char.ConvertToUtf32(text, offset));
                if (Char.IsHighSurrogate(text[offset])) offset++;
                uint glyph;
                if (rdlc_font_get_glyph(token, scalar, out glyph) != 0)
                    throw new InvalidOperationException(Error());
                scalarCount++;
                if (glyph != 0) glyphs.Add(checked((ushort)glyph));
                if (glyph == 0 && !Char.IsWhiteSpace(text, offset) && !Char.IsControl(text, offset))
                    missing.Add(scalar);
            }
            Console.WriteLine("  " + label + " FACE: " + FontBridge.DescribeFont(token));
            Console.WriteLine("  scalars=" + scalarCount + " missingPrintable=" + missing.Count);
            FontBridge.Metrics metrics = FontBridge.GetMetrics(token);
            Console.WriteLine("  unitsPerEm=" + metrics.UnitsPerEm + " fsType=0x" +
                metrics.FsType.ToString("X4", CultureInfo.InvariantCulture));
            if (missing.Count != 0)
                Console.WriteLine("  MISSING: " + String.Join(" ", missing.Select(
                    scalar => "U+" + scalar.ToString("X4", CultureInfo.InvariantCulture) +
                    "(" + Char.ConvertFromUtf32(checked((int)scalar)) + ")")));
            if (missing.Count == 0 && glyphs.Count != 0)
            {
                byte[] subset = FontBridge.PackageFont(token, glyphs.ToArray());
                if (subset.Length < 12 || subset[0] != 0 || subset[1] != 1 ||
                    subset[2] != 0 || subset[3] != 0)
                    throw new InvalidOperationException("Unexpected non-TrueType subset");
                Console.WriteLine("  nominalGlyphSubsetBytes=" + subset.Length);
            }
        }
        finally { FontBridge.ReleaseFontToken(token); }
    }

    private static void Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 3)
            throw new ArgumentException("Expected report.rdlc [candidate-family-or-file [textbox-name]]");
        Console.OutputEncoding = new UTF8Encoding(false);
        XDocument document = XDocument.Load(args[0]);
        XNamespace ns = document.Root.Name.Namespace;
        foreach (XElement box in document.Descendants(ns + "Textbox"))
        {
            if (args.Length == 3 && (string)box.Attribute("Name") != args[2]) continue;
            foreach (XElement run in box.Descendants(ns + "TextRun"))
            {
                string family = (string)run.Element(ns + "Style").Element(ns + "FontFamily");
                string size = (string)run.Element(ns + "Style").Element(ns + "FontSize");
                if (size == null || !size.EndsWith("pt", StringComparison.Ordinal))
                    throw new InvalidOperationException("Audit expects explicit point font sizes");
                float points = Single.Parse(size.Substring(0, size.Length - 2), CultureInfo.InvariantCulture);
                string text = (string)run.Element(ns + "Value");
                Console.WriteLine("TEXTBOX " + (string)box.Attribute("Name") + " REQUEST " + family);
                AuditFace("requested", family, false, false, points, text);
                using (Font font = new Font(family, points, FontStyle.Regular))
                {
                    Console.WriteLine("  Mono FontFamily.Name=" + font.FontFamily.Name +
                        " OriginalFontName=" + font.OriginalFontName);
                    AuditFace("Mono-resolved", font.FontFamily.Name, font.Bold, font.Italic, points, text);
                }
                if (args.Length >= 2) AuditFace("candidate", args[1], false, false, points, text);
            }
        }
    }
}
