// Minimal stub for System.Drawing.Common to satisfy type resolution on Linux.
// Only implements the types actually referenced by BC service tier DLLs.
// All methods are no-ops or return sensible defaults.
//
// IMPORTANT: Do NOT define types from System.Drawing.Primitives here (Color, Point,
// PointF, Rectangle, RectangleF, Size, SizeF). Those come from the framework and
// must resolve through the forwarding chain to netstandard-merged.dll to avoid
// type identity duplication.

using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing; // Color, Point, Rectangle etc. from framework

// ============================================================================
// System.Drawing (types unique to System.Drawing.Common)
// ============================================================================

namespace System.Drawing
{
    public abstract class Image : IDisposable
    {
        // Real content where we have it: images constructed from a stream keep the source
        // bytes so Save() round-trips them; images constructed from dimensions synthesize
        // a valid PNG/JPEG. A no-op Save produced zero-byte files, which BC's attachment
        // import rejects with "The selected file '<x>' has no content."
        internal byte[]? SourceBytes;
        internal int StubWidth;
        internal int StubHeight;
        // True when the content carries a known image signature (or the object was
        // synthesized from dimensions). Image.FromStream throws when this is false,
        // matching Windows GDI+ — see FromStream.
        internal bool LooksLikeImage = true;

        public virtual int Width => StubWidth;
        public virtual int Height => StubHeight;
        public float HorizontalResolution => 96f;
        public float VerticalResolution => 96f;
        public Guid[] FrameDimensionsList => Array.Empty<Guid>();
        public int[] PropertyIdList => Array.Empty<int>();
        public Drawing.Imaging.ImageFormat RawFormat => Drawing.Imaging.ImageFormat.Png;

        public int GetFrameCount(Drawing.Imaging.FrameDimension dimension) => 0;
        public void SelectActiveFrame(Drawing.Imaging.FrameDimension dimension, int frameIndex) { }

        public void Save(Stream stream, Drawing.Imaging.ImageFormat format)
            => StubImageWriter.Write(stream, this, format?.Guid ?? default);
        public void Save(Stream stream, Drawing.Imaging.ImageCodecInfo encoder,
            Drawing.Imaging.EncoderParameters? encoderParams)
            => StubImageWriter.Write(stream, this, encoder?.FormatID ?? default);
        public void SaveAdd(Drawing.Imaging.EncoderParameters encoderParams) { }

        public Drawing.Imaging.PropertyItem? GetPropertyItem(int propid) => null;
        public void SetPropertyItem(Drawing.Imaging.PropertyItem propitem) { }

        // Faithful to Windows GDI+: FromStream THROWS ArgumentException for content that
        // isn't a recognized image. BC relies on that contract for content sniffing —
        // NavMediaFactory.ProcessMediaObject probes unknown media with Image.FromStream
        // and falls back to application/octet-stream when it throws. A never-throwing
        // stub made BC treat arbitrary text/binary media as images and re-encode them
        // into garbage (issue #18: tenant report layout import produced an empty layout).
        public static Image FromStream(Stream stream) => FromStream(stream, false);
        public static Image FromStream(Stream stream, bool useEmbeddedColorManagement)
        {
            var bmp = new Bitmap(stream);
            if (!bmp.LooksLikeImage)
                throw new ArgumentException("Parameter is not valid.");
            return bmp;
        }

        public void RotateFlip(RotateFlipType rotateFlipType) { }

        public void Dispose() { }
        protected virtual void Dispose(bool disposing) { }
    }

    public enum RotateFlipType
    {
        RotateNoneFlipNone = 0, Rotate90FlipNone = 1, Rotate180FlipNone = 2, Rotate270FlipNone = 3,
        RotateNoneFlipX = 4, Rotate90FlipX = 5, Rotate180FlipX = 6, Rotate270FlipX = 7,
        RotateNoneFlipY = Rotate180FlipX, Rotate90FlipY = Rotate270FlipX,
        Rotate180FlipY = RotateNoneFlipX, Rotate270FlipY = Rotate90FlipX,
        RotateNoneFlipXY = Rotate180FlipNone, Rotate90FlipXY = Rotate270FlipNone,
        Rotate180FlipXY = RotateNoneFlipNone, Rotate270FlipXY = Rotate90FlipNone,
    }

    public sealed class Icon : IDisposable
    {
        public Icon(Stream stream) { }
        public Icon(Stream stream, int width, int height) { }
        public Icon(string fileName) { }
        public Icon(Icon original, int width, int height) { }
        public int Width => 32;
        public int Height => 32;
        public Bitmap ToBitmap() => new Bitmap(Width, Height);
        public void Dispose() { }
    }

    public sealed class Bitmap : Image
    {
        public Bitmap(int width, int height)
        {
            StubWidth = width > 0 ? width : 1;
            StubHeight = height > 0 ? height : 1;
        }

        public Bitmap(Stream stream)
        {
            // Restore the stream position after reading. BC probes unknown media with
            // Image.FromStream and, when that throws, hands the SAME stream to a binary
            // fallback that copies from the CURRENT position without rewinding
            // (NavMediaDocument..ctor does mediaSourceStream.CopyTo with no Seek).
            // Consuming the stream without restoring made that fallback capture zero
            // bytes — the actual mechanism behind issue #18's "empty Custom layout".
            long pos = -1;
            try
            {
                if (stream.CanSeek) pos = stream.Position;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                SourceBytes = ms.ToArray();
                LooksLikeImage = StubImageWriter.HasImageSignature(SourceBytes);
                var (w, h) = StubImageWriter.SniffDimensions(SourceBytes);
                StubWidth = w;
                StubHeight = h;
            }
            catch
            {
                LooksLikeImage = false;
                StubWidth = 1;
                StubHeight = 1;
            }
            finally
            {
                if (pos >= 0)
                    try { stream.Position = pos; } catch { }
            }
        }

        public Bitmap(Image original)
        {
            SourceBytes = original?.SourceBytes;
            StubWidth = original?.StubWidth ?? 1;
            StubHeight = original?.StubHeight ?? 1;
        }

        public Bitmap(Image original, int width, int height)
        {
            // A resize drops the source bytes — Save() synthesizes at the new dimensions.
            StubWidth = width > 0 ? width : 1;
            StubHeight = height > 0 ? height : 1;
        }
        public void SetResolution(float xDpi, float yDpi) { }
        public void MakeTransparent() { }
        public void MakeTransparent(Color transparentColor) { }
        public Color GetPixel(int x, int y) => Color.Empty;
        public void SetPixel(int x, int y, Color color) { }
    }

    // Reflection-only bridge to SkiaSharp. Deliberately NOT a compile-time reference:
    // DrawingStub is loaded in two very different places — into Add-Ins for the AL
    // compile (metadata only, Save() is never called) and into the NST at runtime via
    // SetupStubWithResolver — and only the second one has SkiaSharp next to it. A hard
    // reference would turn "no Skia here" into a load failure of System.Drawing.Common
    // itself. It also has to stay optional at runtime: the entrypoint links
    // libSkiaSharp.so ONLY when it can version-match the artifact's managed
    // SkiaSharp.dll, so on a version it has no native for, Skia is present as metadata
    // and unusable. Every failure path here falls back to the caller's source bytes.
    internal static class SkiaCodec
    {
        private static bool _probed;
        private static MethodInfo? _decode;      // SKBitmap.Decode(byte[])
        private static MethodInfo? _fromBitmap;  // SKImage.FromBitmap(SKBitmap)
        private static MethodInfo? _encode;      // SKImage.Encode(SKEncodedImageFormat, int)
        private static MethodInfo? _toArray;     // SKData.ToArray()
        private static Type? _formatEnum;

        private static readonly Guid JpegFormat = new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e");
        private static readonly Guid ExifFormat = new Guid("b96b3cb2-0728-11d3-9d7b-0000f81ef32e");

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                              .FirstOrDefault(x => x.GetName().Name == "SkiaSharp")
                          ?? Assembly.Load("SkiaSharp");
                var bitmap = asm.GetType("SkiaSharp.SKBitmap");
                var image = asm.GetType("SkiaSharp.SKImage");
                var data = asm.GetType("SkiaSharp.SKData");
                _formatEnum = asm.GetType("SkiaSharp.SKEncodedImageFormat");
                if (bitmap == null || image == null || data == null || _formatEnum == null) return;

                _decode = bitmap.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static,
                                           null, new[] { typeof(byte[]) }, null);
                _fromBitmap = image.GetMethod("FromBitmap", BindingFlags.Public | BindingFlags.Static,
                                              null, new[] { bitmap }, null);
                _encode = image.GetMethod("Encode", BindingFlags.Public | BindingFlags.Instance,
                                          null, new[] { _formatEnum, typeof(int) }, null);
                _toArray = data.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance,
                                          Type.EmptyTypes);
            }
            catch
            {
                _decode = null; // any failure: stay unavailable, callers pass bytes through
            }
        }

        /// <summary>Re-encode <paramref name="source"/>, or null when Skia is unavailable
        /// or cannot handle this content — the caller then writes the source verbatim.</summary>
        internal static byte[]? TryReencode(byte[] source, Guid formatGuid)
        {
            Probe();
            if (_decode == null || _fromBitmap == null || _encode == null || _toArray == null || _formatEnum == null)
                return null;

            // Skia encodes PNG and JPEG. Everything else GDI+ can save (GIF, BMP, TIFF,
            // ICO, EMF, WMF) is left alone rather than silently converted to a different
            // format, which would be a bigger divergence than passing the bytes through.
            string formatName = (formatGuid == JpegFormat || formatGuid == ExifFormat) ? "Jpeg" : "Png";

            object? bmp = null, img = null, data = null;
            try
            {
                bmp = _decode.Invoke(null, new object[] { source });
                if (bmp == null) return null;               // not decodable by Skia
                img = _fromBitmap.Invoke(null, new[] { bmp });
                if (img == null) return null;
                var fmt = Enum.Parse(_formatEnum, formatName);
                data = _encode.Invoke(img, new object[] { fmt, 100 });
                if (data == null) return null;              // encoder refused this format
                var bytes = _toArray.Invoke(data, null) as byte[];
                return (bytes != null && bytes.Length > 0) ? bytes : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                (data as IDisposable)?.Dispose();
                (img as IDisposable)?.Dispose();
                (bmp as IDisposable)?.Dispose();
            }
        }
    }

    // PNG to PNG the way Windows GDI+ writes it (issue #135). GDI+ decodes a grayscale or
    // truecolor PNG into a 32-bit ARGB bitmap, and its PNG encoder (libpng) writes that back
    // as 8-bit RGBA with an sRGB chunk (intent 0) and a gAMA chunk (45455), no pHYs. The
    // pixel rows go through libpng 1.6's adaptive filter choice and classic zlib with the
    // window libpng sizes to the image.
    //
    // Measured against Windows (BC 27.5 and 29.0, official containers) for one image only:
    // the corpus's 1x1 grayscale PNG, which comes out byte for byte the same here. zlib
    // levels 2 to 5 all give that output; the level GDI+ uses is not known, so larger images
    // may compress differently. Only 8-bit, non-interlaced gray, gray+alpha, RGB and RGBA
    // sources are handled, and only without colour-management chunks (gAMA other than
    // 45455, cHRM, iCCP), which GDI+ applies while decoding. Everything else returns null
    // and takes the older path.
    internal static class GdiPlusPng
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        internal static byte[]? TryReencode(byte[] source)
        {
            try
            {
                var rgba = Decode(source, out int width, out int height);
                if (rgba == null) return null;
                return Encode(rgba, width, height);
            }
            catch
            {
                return null;
            }
        }

        // Returns straight (not premultiplied) RGBA, or null for anything out of scope.
        private static byte[]? Decode(byte[] d, out int width, out int height)
        {
            width = height = 0;
            if (d.Length < 8) return null;
            for (int i = 0; i < 8; i++) if (d[i] != Signature[i]) return null;

            int depth = -1, colorType = -1, interlace = -1;
            byte[]? trns = null;
            using var idat = new MemoryStream();
            int p = 8;
            while (p + 12 <= d.Length)
            {
                int len = BE32(d, p);
                if (len < 0 || p + 12 + len > d.Length) return null;
                string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                int data = p + 8;
                switch (type)
                {
                    case "IHDR":
                        if (len != 13) return null;
                        width = BE32(d, data);
                        height = BE32(d, data + 4);
                        depth = d[data + 8];
                        colorType = d[data + 9];
                        interlace = d[data + 12];
                        break;
                    case "IDAT":
                        idat.Write(d, data, len);
                        break;
                    case "tRNS":
                        trns = new byte[len];
                        Buffer.BlockCopy(d, data, trns, 0, len);
                        break;
                    case "gAMA":
                        if (len != 4 || BE32(d, data) != 45455) return null;
                        break;
                    case "cHRM":
                    case "iCCP":
                        return null;
                }
                p += 12 + len;
                if (type == "IEND") break;
            }

            if (depth != 8 || interlace != 0 || width <= 0 || height <= 0) return null;
            int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0) return null;
            if ((long)width * height > 16_000_000) return null;

            int stride = width * channels;
            var raw = new byte[(long)height * (stride + 1)];
            idat.Position = 0;
            using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress))
            {
                // Image data that ends early leaves the rest of the image zero, as GDI+
                // accepts it (issue #161: a 1x1 RGBA PNG whose IDAT holds 4 of its 5 bytes is
                // stored as 99 bytes on Windows). Only the length was compared with Windows;
                // the pixel values GDI+ fills in are not known.
                int read = 0;
                while (read < raw.Length)
                {
                    int n = z.Read(raw, read, raw.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == 0) return null;
            }

            var prev = new byte[stride];
            var cur = new byte[stride];
            var rgba = new byte[(long)width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * (stride + 1);
                byte filter = raw[rowStart];
                Buffer.BlockCopy(raw, rowStart + 1, cur, 0, stride);
                if (!Unfilter(filter, cur, prev, channels)) return null;

                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    int s = x * channels;
                    switch (colorType)
                    {
                        case 0:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = cur[s];
                            rgba[o + 3] = (trns != null && trns.Length >= 2 && cur[s] == trns[1] && trns[0] == 0) ? (byte)0 : (byte)255;
                            break;
                        case 4:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = cur[s];
                            rgba[o + 3] = cur[s + 1];
                            break;
                        case 2:
                            rgba[o] = cur[s]; rgba[o + 1] = cur[s + 1]; rgba[o + 2] = cur[s + 2];
                            bool transparent = trns != null && trns.Length >= 6
                                && trns[0] == 0 && trns[1] == cur[s] && trns[2] == 0 && trns[3] == cur[s + 1]
                                && trns[4] == 0 && trns[5] == cur[s + 2];
                            rgba[o + 3] = transparent ? (byte)0 : (byte)255;
                            break;
                        default:
                            rgba[o] = cur[s]; rgba[o + 1] = cur[s + 1]; rgba[o + 2] = cur[s + 2]; rgba[o + 3] = cur[s + 3];
                            break;
                    }
                }
                (prev, cur) = (cur, prev);
            }
            return rgba;
        }

        private static bool Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
        {
            int n = cur.Length;
            switch (filter)
            {
                case 0: return true;
                case 1:
                    for (int i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    return true;
                case 2:
                    for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    return true;
                case 3:
                    for (int i = 0; i < n; i++)
                        cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                    return true;
                case 4:
                    for (int i = 0; i < n; i++)
                        cur[i] = (byte)(cur[i] + Paeth(i >= bpp ? cur[i - bpp] : 0, prev[i], i >= bpp ? prev[i - bpp] : 0));
                    return true;
                default: return false;
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = b - c, pc = a - c;
            int pa = Math.Abs(p), pb = Math.Abs(pc);
            pc = Math.Abs(p + pc);
            return (pa <= pb && pa <= pc) ? a : (pb <= pc) ? b : c;
        }

        private static byte[] Encode(byte[] rgba, int width, int height)
        {
            const int bpp = 4;
            int rowBytes = width * bpp;

            // libpng 1.6 png_write_start_row: filters that need a row above are dropped for a
            // one-row image, filters that need a pixel to the left for a one-column image.
            bool sub = width > 1, up = height > 1, avg = width > 1 && height > 1, paeth = avg;

            var filtered = new byte[(long)height * (rowBytes + 1)];
            var prev = new byte[rowBytes];
            var row = new byte[rowBytes];
            var best = new byte[rowBytes];
            var trial = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * rowBytes, row, 0, rowBytes);
                // png_write_find_filter: smallest sum of the bytes read as signed, ties keep
                // the earlier filter in the order None, Sub, Up, Average, Paeth.
                byte bestFilter = 0;
                Buffer.BlockCopy(row, 0, best, 0, rowBytes);
                long mins = Sum(best);
                for (byte f = 1; f <= 4; f++)
                {
                    if ((f == 1 && !sub) || (f == 2 && !up) || (f == 3 && !avg) || (f == 4 && !paeth)) continue;
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int pred = f switch { 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                        trial[i] = (byte)(row[i] - pred);
                    }
                    long sum = Sum(trial);
                    if (sum < mins)
                    {
                        mins = sum;
                        bestFilter = f;
                        (best, trial) = (trial, best);
                    }
                }
                long at = (long)y * (rowBytes + 1);
                filtered[at] = bestFilter;
                Buffer.BlockCopy(best, 0, filtered, (int)at + 1, rowBytes);
                (prev, row) = (row, prev);
            }

            // png_deflate_claim: the smallest window that still covers the image data.
            int windowBits = 15;
            long dataSize = filtered.Length;
            if (dataSize <= 16384)
            {
                long half = 1L << (windowBits - 1);
                while (dataSize + 262 <= half) { half >>= 1; windowBits--; }
            }
            if (windowBits == 8) windowBits = 9;

            byte[] zdata = Zlib.Deflate(filtered, Level, windowBits, 8, ZFiltered);

            var ihdr = new byte[13];
            PutBE32(ihdr, 0, width);
            PutBE32(ihdr, 4, height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // RGBA

            using var png = new MemoryStream();
            png.Write(Signature, 0, Signature.Length);
            Chunk(png, "IHDR", ihdr, 0, ihdr.Length);
            Chunk(png, "sRGB", new byte[] { 0 }, 0, 1);
            Chunk(png, "gAMA", new byte[] { 0, 0, 0xB1, 0x8F }, 0, 4);
            // libpng writes IDAT whenever its 8 KiB compression buffer is full.
            for (int o = 0; o < zdata.Length; o += 8192)
                Chunk(png, "IDAT", zdata, o, Math.Min(8192, zdata.Length - o));
            Chunk(png, "IEND", Array.Empty<byte>(), 0, 0);
            return png.ToArray();
        }

        // zlib level. Levels 2 to 5 give the same bytes on the one image measured on Windows.
        private const int Level = 5;
        private const int ZFiltered = 1;

        private static long Sum(byte[] row)
        {
            long s = 0;
            foreach (byte v in row) s += v < 128 ? v : 256 - v;
            return s;
        }

        private static int BE32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

        private static void PutBE32(byte[] d, int i, int v)
        {
            d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16); d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v;
        }

        private static void Chunk(Stream s, string type, byte[] data, int offset, int length)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var len = new byte[4];
            PutBE32(len, 0, length);
            s.Write(len, 0, 4);
            s.Write(t, 0, 4);
            s.Write(data, offset, length);
            var body = new byte[length];
            Buffer.BlockCopy(data, offset, body, 0, length);
            uint crc = StubImageWriter.Crc32(t, body);
            var c = new byte[4];
            PutBE32(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }
    }

    // Classic zlib (libz.so.1), because libpng uses it and its output is what GDI+ writes.
    // .NET's own deflate is a different implementation and compresses to different bytes.
    // When libz cannot be loaded, the caller gets an exception and falls back.
    internal static class Zlib
    {
        [Runtime.InteropServices.StructLayout(Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ZStream
        {
            public IntPtr next_in; public uint avail_in; public UIntPtr total_in;
            public IntPtr next_out; public uint avail_out; public UIntPtr total_out;
            public IntPtr msg; public IntPtr state;
            public IntPtr zalloc; public IntPtr zfree; public IntPtr opaque;
            public int data_type; public UIntPtr adler; public UIntPtr reserved;
        }

        [Runtime.InteropServices.DllImport("libz.so.1", EntryPoint = "deflateInit2_")]
        private static extern int DeflateInit2(ref ZStream strm, int level, int method, int windowBits, int memLevel, int strategy,
            [Runtime.InteropServices.MarshalAs(Runtime.InteropServices.UnmanagedType.LPStr)] string version, int streamSize);
        [Runtime.InteropServices.DllImport("libz.so.1", EntryPoint = "deflate")]
        private static extern int DeflateCall(ref ZStream strm, int flush);
        [Runtime.InteropServices.DllImport("libz.so.1", EntryPoint = "deflateEnd")]
        private static extern int DeflateEnd(ref ZStream strm);
        [Runtime.InteropServices.DllImport("libz.so.1", EntryPoint = "zlibVersion")]
        private static extern IntPtr ZlibVersion();

        internal static byte[] Deflate(byte[] input, int level, int windowBits, int memLevel, int strategy)
        {
            var output = new byte[input.Length + input.Length / 1000 + 64 + 16];
            var inHandle = Runtime.InteropServices.GCHandle.Alloc(input, Runtime.InteropServices.GCHandleType.Pinned);
            var outHandle = Runtime.InteropServices.GCHandle.Alloc(output, Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var z = new ZStream
                {
                    next_in = inHandle.AddrOfPinnedObject(), avail_in = (uint)input.Length,
                    next_out = outHandle.AddrOfPinnedObject(), avail_out = (uint)output.Length,
                };
                string version = Runtime.InteropServices.Marshal.PtrToStringAnsi(ZlibVersion()) ?? "1.2.11";
                int rc = DeflateInit2(ref z, level, 8, windowBits, memLevel, strategy, version,
                    Runtime.InteropServices.Marshal.SizeOf<ZStream>());
                if (rc != 0) throw new InvalidOperationException("deflateInit2_ returned " + rc);
                try
                {
                    rc = DeflateCall(ref z, 4); // Z_FINISH
                    if (rc != 1) throw new InvalidOperationException("deflate returned " + rc); // Z_STREAM_END
                    var result = new byte[(long)z.total_out];
                    Buffer.BlockCopy(output, 0, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    DeflateEnd(ref z);
                }
            }
            finally
            {
                inHandle.Free();
                outHandle.Free();
            }
        }
    }

    // Re-encodes through SkiaSharp when it is available, so a Save() round-trip
    // behaves the way GDI+ does: the bytes that come out are a fresh encode, not the
    // bytes that went in. Falls back to writing the source bytes verbatim when Skia
    // cannot be used, and synthesizes a valid image when the Image was constructed
    // from dimensions rather than from a stream. Not gated to specific BC call sites
    // — anything that saves an Image gets non-empty, well-formed output.
    //
    // Why this matters (issue #67): BC's NavMediaImage.Bytes() copies the ORIGINAL
    // stream when it still holds one, and only calls Image.Save() on the path where
    // it deliberately re-encodes. So Save() is reached exactly where a real tier
    // produces different bytes from the ones imported. Passing the source through
    // there made a Media round-trip byte-preserving on Linux and re-encoded on
    // Windows — a test asserting "ExportStream returns what I imported" passed here
    // and failed on a real tier.
    //
    // PNG saved as PNG goes through GdiPlusPng first, which writes what GDI+ writes for the
    // sources it handles (issue #135). Everything else goes through Skia, whose encoder is
    // its own: the stream is re-encoded, but the bytes differ from GDI+'s.
    internal static class StubImageWriter
    {
        private static readonly Guid JpegGuid = new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e");
        private static readonly Guid ExifGuid = new Guid("b96b3cb2-0728-11d3-9d7b-0000f81ef32e");
        private static readonly Guid PngGuid = new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e");

        // Smallest well-formed baseline JPEG (1×1 white). Synthesizing JPEG at arbitrary
        // dimensions isn't worth a DCT encoder — consumers that care about dimensions get
        // them from Image.Width/Height, not by re-decoding.
        private static readonly byte[] MinimalJpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwc" +
            "KDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAA" +
            "AAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==");

        internal static void Write(Stream stream, Image image, Guid formatGuid)
        {
            var src = image?.SourceBytes;
            if (src != null && src.Length > 0)
            {
                var reencoded = (formatGuid == PngGuid ? GdiPlusPng.TryReencode(src) : null)
                                ?? SkiaCodec.TryReencode(src, formatGuid);
                var outBytes = reencoded ?? src;
                stream.Write(outBytes, 0, outBytes.Length);
                return;
            }
            if (formatGuid == JpegGuid || formatGuid == ExifGuid)
            {
                stream.Write(MinimalJpeg, 0, MinimalJpeg.Length);
                return;
            }
            var png = BuildPng(image?.StubWidth ?? 1, image?.StubHeight ?? 1);
            stream.Write(png, 0, png.Length);
        }

        // Same signature set Windows GDI+ accepts (BMP/GIF/JPEG/PNG/TIFF/ICO/EMF/WMF).
        // Content matching none of these must make Image.FromStream throw, like GDI+.
        internal static bool HasImageSignature(byte[] d)
        {
            if (d == null || d.Length < 8) return false;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;      // PNG
            if (d[0] == 0xFF && d[1] == 0xD8) return true;                                       // JPEG
            if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return true;           // GIF
            if (d[0] == 'B' && d[1] == 'M') return true;                                         // BMP
            if ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0)
             || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42)) return true;            // TIFF
            if (d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) return true;                   // ICO
            if (d[0] == 0x01 && d[1] == 0x00 && d[2] == 0x00 && d[3] == 0x00
             && d.Length > 43 && d[40] == 0x20 && d[41] == 0x45 && d[42] == 0x4D && d[43] == 0x46) return true; // EMF
            if (d[0] == 0xD7 && d[1] == 0xCD && d[2] == 0xC6 && d[3] == 0x9A) return true;       // WMF (placeable)
            return false;
        }

        internal static (int, int) SniffDimensions(byte[] d)
        {
            try
            {
                // PNG: 8-byte signature, IHDR width/height big-endian at 16/20
                if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
                    return (BE32(d, 16), BE32(d, 20));
                // GIF: little-endian u16 at 6/8
                if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
                    return (d[6] | (d[7] << 8), d[8] | (d[9] << 8));
                // BMP: little-endian i32 at 18/22
                if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M')
                    return (d[18] | (d[19] << 8) | (d[20] << 16) | (d[21] << 24),
                            Math.Abs(d[22] | (d[23] << 8) | (d[24] << 16) | (d[25] << 24)));
                // JPEG: scan for SOFn marker (C0–CF except C4/C8/CC), height/width big-endian
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 9 < d.Length && d[i] == 0xFF)
                    {
                        byte marker = d[i + 1];
                        int len = (d[i + 2] << 8) | d[i + 3];
                        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                        i += 2 + len;
                    }
                }
            }
            catch { }
            return (1, 1);
        }

        private static int BE32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

        // Minimal but fully valid PNG: 8-bit RGB, opaque white, correct CRC32 + zlib/Adler32.
        private static byte[] BuildPng(int width, int height)
        {
            if (width <= 0) width = 1;
            if (height <= 0) height = 1;
            // Cap synthesis so a pathological Bitmap(100000,100000) can't allocate GBs.
            if ((long)width * height > 16_000_000) { width = Math.Min(width, 4000); height = Math.Min(height, 4000); }

            var raw = new byte[height * (1 + 3 * width)];
            for (int y = 0; y < height; y++)
            {
                int row = y * (1 + 3 * width);
                // raw[row] = 0 (filter: None)
                for (int x = 1; x <= 3 * width; x++) raw[row + x] = 0xFF; // white
            }

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(raw, 0, raw.Length);
                compressed = ms.ToArray();
            }
            var idat = new byte[2 + compressed.Length + 4];
            idat[0] = 0x78; idat[1] = 0x9C; // zlib header
            Buffer.BlockCopy(compressed, 0, idat, 2, compressed.Length);
            uint adler = Adler32(raw);
            idat[^4] = (byte)(adler >> 24); idat[^3] = (byte)(adler >> 16);
            idat[^2] = (byte)(adler >> 8); idat[^1] = (byte)adler;

            var ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // color type: truecolor RGB
            // 10..12: compression/filter/interlace = 0

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", idat);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var t = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }, 0, 4);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(t, data);
            s.Write(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte t in data) { a = (a + t) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        private static uint[]? _crcTable;
        internal static uint Crc32(byte[] type, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte t in type) crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
            foreach (byte t in data) crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }

    public static class ColorTranslator
    {
        public static Color FromHtml(string htmlColor) => Color.Empty;
        public static Color FromOle(int oleColor) => Color.Empty;
        public static Color FromWin32(int win32Color) => Color.Empty;
        public static string ToHtml(Color c) => string.Empty;
        public static int ToOle(Color c) => 0;
        public static int ToWin32(Color c) => 0;
    }

    public sealed class FontFamily : IDisposable
    {
        public string Name => string.Empty;
        public FontFamily() { }
        public FontFamily(string name) { }
        public void Dispose() { }
    }

    public sealed class Font : IDisposable
    {
        public string Name { get; }
        public float Size { get; }
        public FontStyle Style { get; }

        public Font(string familyName, float emSize)
        {
            Name = familyName ?? string.Empty;
            Size = emSize;
            Style = FontStyle.Regular;
        }

        public Font(string familyName, float emSize, FontStyle style)
        {
            Name = familyName ?? string.Empty;
            Size = emSize;
            Style = style;
        }

        public Font(FontFamily family, float emSize)
        {
            Name = family?.Name ?? string.Empty;
            Size = emSize;
            Style = FontStyle.Regular;
        }

        public void Dispose() { }
    }

    [Flags]
    public enum FontStyle
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikeout = 8,
    }

    public static class SystemFonts
    {
        public static Font IconTitleFont => new Font("Sans", 10f);
        public static Font DefaultFont => new Font("Sans", 10f);
    }

    public abstract class Brush : IDisposable
    {
        public void Dispose() { }
    }

    public sealed class SolidBrush : Brush
    {
        public Color Color { get; set; }
        public SolidBrush(Color color) { Color = color; }
    }

    public static class Brushes
    {
        public static Brush White { get; } = new SolidBrush(Color.White);
        public static Brush Black { get; } = new SolidBrush(Color.Black);
        public static Brush Transparent { get; } = new SolidBrush(Color.Transparent);
        public static Brush Red { get; } = new SolidBrush(Color.Red);
        public static Brush Green { get; } = new SolidBrush(Color.Green);
        public static Brush Blue { get; } = new SolidBrush(Color.Blue);
        public static Brush Gray { get; } = new SolidBrush(Color.Gray);
        public static Brush LightGray { get; } = new SolidBrush(Color.LightGray);
    }

    public static class Pens
    {
        public static Pen Black { get; } = new Pen(Color.Black);
        public static Pen White { get; } = new Pen(Color.White);
    }

    public sealed class Pen : IDisposable
    {
        public Color Color { get; set; }
        public float Width { get; set; }

        public Pen(Color color) { Color = color; Width = 1f; }
        public Pen(Color color, float width) { Color = color; Width = width; }
        public void Dispose() { }
    }

    public sealed class StringFormat : IDisposable
    {
        public StringAlignment Alignment { get; set; }
        public StringAlignment LineAlignment { get; set; }
        public void Dispose() { }
    }

    public enum StringAlignment
    {
        Near = 0,
        Center = 1,
        Far = 2,
    }

    public sealed class Graphics : IDisposable
    {
        public Drawing2D.SmoothingMode SmoothingMode { get; set; }
        public Drawing2D.InterpolationMode InterpolationMode { get; set; }
        public Drawing2D.PixelOffsetMode PixelOffsetMode { get; set; }
        public Drawing2D.CompositingQuality CompositingQuality { get; set; }
        public Drawing2D.CompositingMode CompositingMode { get; set; }
        public Text.TextRenderingHint TextRenderingHint { get; set; }
        public float DpiX => 96f;
        public float DpiY => 96f;

        public static Graphics FromImage(Image image) => new Graphics();

        public void Clear(Color color) { }
        public void FillRectangle(Brush brush, RectangleF rect) { }
        public void FillRectangle(Brush brush, Rectangle rect) { }
        public void DrawString(string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format) { }
        public void DrawString(string s, Font font, Brush brush, PointF point) { }
        public void DrawImage(Image image, Point point) { }
        public void DrawImage(Image image, int x, int y) { }
        public void DrawImage(Image image, Rectangle destRect) { }
        public void DrawImage(Image image, int x, int y, int width, int height) { }
        public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit) { }
        public void Flush() { }
        public void Dispose() { }
    }

    public enum GraphicsUnit
    {
        World = 0,
        Display = 1,
        Pixel = 2,
        Point = 3,
        Inch = 4,
        Document = 5,
        Millimeter = 6,
    }

    // TypeConverter for ImageFormat — BC instantiates this during schema sync for media fields
    public class ImageFormatConverter : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override bool CanConvertTo(System.ComponentModel.ITypeDescriptorContext? context, Type? destinationType)
            => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context,
            System.Globalization.CultureInfo? culture, object value)
        {
            if (value is string s)
            {
                // Return known formats by name
                return s.ToLowerInvariant() switch
                {
                    "bmp" => Imaging.ImageFormat.Bmp,
                    "jpeg" or "jpg" => Imaging.ImageFormat.Jpeg,
                    "png" => Imaging.ImageFormat.Png,
                    "gif" => Imaging.ImageFormat.Gif,
                    "tiff" or "tif" => Imaging.ImageFormat.Tiff,
                    "icon" or "ico" => Imaging.ImageFormat.Icon,
                    "emf" => Imaging.ImageFormat.Emf,
                    "wmf" => Imaging.ImageFormat.Wmf,
                    _ => Imaging.ImageFormat.Png
                };
            }
            return base.ConvertFrom(context, culture, value);
        }

        public override object? ConvertTo(System.ComponentModel.ITypeDescriptorContext? context,
            System.Globalization.CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is Imaging.ImageFormat fmt)
                return fmt.Guid.ToString();
            return base.ConvertTo(context, culture, value, destinationType);
        }
    }
}

// ============================================================================
// System.Drawing.Text
// ============================================================================

namespace System.Drawing.Text
{
    public abstract class FontCollection : IDisposable
    {
        public FontFamily[] Families => Array.Empty<FontFamily>();
        public void Dispose() { }
    }

    public sealed class InstalledFontCollection : FontCollection
    {
        public InstalledFontCollection() { }
    }

    public enum TextRenderingHint
    {
        SystemDefault = 0,
        SingleBitPerPixelGridFit = 1,
        SingleBitPerPixel = 2,
        AntiAliasGridFit = 3,
        AntiAlias = 4,
        ClearTypeGridFit = 5,
    }
}

// ============================================================================
// System.Drawing.Drawing2D
// ============================================================================

namespace System.Drawing.Drawing2D
{
    public enum SmoothingMode
    {
        Invalid = -1, Default = 0, HighSpeed = 1, HighQuality = 2, None = 3, AntiAlias = 4,
    }

    public enum InterpolationMode
    {
        Invalid = -1, Default = 0, Low = 1, High = 2, Bilinear = 3, Bicubic = 4,
        NearestNeighbor = 5, HighQualityBilinear = 6, HighQualityBicubic = 7,
    }

    public enum PixelOffsetMode
    {
        Invalid = -1, Default = 0, HighSpeed = 1, HighQuality = 2, None = 3, Half = 4,
    }

    public enum CompositingQuality
    {
        Invalid = -1, Default = 0, HighSpeed = 1, HighQuality = 2, GammaCorrected = 3, AssumeLinear = 4,
    }

    public enum CompositingMode
    {
        SourceOver = 0,
        SourceCopy = 1,
    }

    public enum MatrixOrder
    {
        Prepend = 0,
        Append = 1,
    }

    // A real implementation, not a no-op: Aspose.PDF (in Microsoft's DocumentProcessor) uses it
    // for image placement while it makes a PDF compliant, and a missing type made every Word
    // layout with section breaks leave "Unable to merge pdf streams" as the last error (#155).
    // GDI+'s 3x2 affine matrix in float math, row-vector convention (p' = p * M), with
    // MatrixOrder.Prepend as the default order, as System.Drawing.Common has it.
    public sealed class Matrix : MarshalByRefObject, IDisposable
    {
        private float _m11, _m12, _m21, _m22, _dx, _dy;

        public Matrix() { _m11 = 1; _m22 = 1; }

        public Matrix(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            _m11 = m11; _m12 = m12; _m21 = m21; _m22 = m22; _dx = dx; _dy = dy;
        }

        public Matrix(System.Numerics.Matrix3x2 matrix)
            : this(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32) { }

        public Matrix(RectangleF rect, PointF[] plgpts)
        {
            ArgumentNullException.ThrowIfNull(plgpts);
            if (plgpts.Length != 3) throw new ArgumentException("Invalid parameter.", nameof(plgpts));
            if (rect.Width == 0 || rect.Height == 0) throw new ArgumentException("Invalid parameter.", nameof(rect));
            _m11 = (plgpts[1].X - plgpts[0].X) / rect.Width;
            _m12 = (plgpts[1].Y - plgpts[0].Y) / rect.Width;
            _m21 = (plgpts[2].X - plgpts[0].X) / rect.Height;
            _m22 = (plgpts[2].Y - plgpts[0].Y) / rect.Height;
            _dx = plgpts[0].X - _m11 * rect.X - _m21 * rect.Y;
            _dy = plgpts[0].Y - _m12 * rect.X - _m22 * rect.Y;
        }

        public Matrix(Rectangle rect, Point[] plgpts)
            : this(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height),
                   (plgpts ?? throw new ArgumentNullException(nameof(plgpts))).Select(p => new PointF(p.X, p.Y)).ToArray()) { }

        public float[] Elements => new[] { _m11, _m12, _m21, _m22, _dx, _dy };

        public System.Numerics.Matrix3x2 MatrixElements
        {
            get => new System.Numerics.Matrix3x2(_m11, _m12, _m21, _m22, _dx, _dy);
            set { _m11 = value.M11; _m12 = value.M12; _m21 = value.M21; _m22 = value.M22; _dx = value.M31; _dy = value.M32; }
        }

        public float OffsetX => _dx;
        public float OffsetY => _dy;
        public bool IsIdentity => _m11 == 1 && _m12 == 0 && _m21 == 0 && _m22 == 1 && _dx == 0 && _dy == 0;
        public bool IsInvertible => _m11 * _m22 - _m12 * _m21 != 0;

        public Matrix Clone() => new Matrix(_m11, _m12, _m21, _m22, _dx, _dy);
        public void Dispose() { }

        public void Reset() { _m11 = 1; _m12 = 0; _m21 = 0; _m22 = 1; _dx = 0; _dy = 0; }

        public void Multiply(Matrix matrix) => Multiply(matrix, MatrixOrder.Prepend);

        public void Multiply(Matrix matrix, MatrixOrder order)
        {
            ArgumentNullException.ThrowIfNull(matrix);
            if (ReferenceEquals(matrix, this)) throw new InvalidOperationException("The matrix cannot be multiplied by itself.");
            Apply(matrix._m11, matrix._m12, matrix._m21, matrix._m22, matrix._dx, matrix._dy, order);
        }

        public void Translate(float offsetX, float offsetY) => Translate(offsetX, offsetY, MatrixOrder.Prepend);
        public void Translate(float offsetX, float offsetY, MatrixOrder order) => Apply(1, 0, 0, 1, offsetX, offsetY, order);

        public void Scale(float scaleX, float scaleY) => Scale(scaleX, scaleY, MatrixOrder.Prepend);
        public void Scale(float scaleX, float scaleY, MatrixOrder order) => Apply(scaleX, 0, 0, scaleY, 0, 0, order);

        public void Shear(float shearX, float shearY) => Shear(shearX, shearY, MatrixOrder.Prepend);
        public void Shear(float shearX, float shearY, MatrixOrder order) => Apply(1, shearY, shearX, 1, 0, 0, order);

        public void Rotate(float angle) => Rotate(angle, MatrixOrder.Prepend);

        public void Rotate(float angle, MatrixOrder order)
        {
            double radians = angle * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
            Apply(cos, sin, -sin, cos, 0, 0, order);
        }

        public void RotateAt(float angle, PointF point) => RotateAt(angle, point, MatrixOrder.Prepend);

        public void RotateAt(float angle, PointF point, MatrixOrder order)
        {
            // Translate the point to the origin, rotate, translate back.
            var op = new Matrix(1, 0, 0, 1, -point.X, -point.Y);
            op.Rotate(angle, MatrixOrder.Append);
            op.Translate(point.X, point.Y, MatrixOrder.Append);
            Apply(op._m11, op._m12, op._m21, op._m22, op._dx, op._dy, order);
        }

        public void Invert()
        {
            float det = _m11 * _m22 - _m12 * _m21;
            if (det == 0) throw new ArgumentException("Invalid parameter.");
            float m11 = _m22 / det, m12 = -_m12 / det, m21 = -_m21 / det, m22 = _m11 / det;
            float dx = -(_dx * m11 + _dy * m21), dy = -(_dx * m12 + _dy * m22);
            _m11 = m11; _m12 = m12; _m21 = m21; _m22 = m22; _dx = dx; _dy = dy;
        }

        public void TransformPoints(PointF[] pts)
        {
            ArgumentNullException.ThrowIfNull(pts);
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(pts[i].X * _m11 + pts[i].Y * _m21 + _dx, pts[i].X * _m12 + pts[i].Y * _m22 + _dy);
        }

        public void TransformPoints(Point[] pts)
        {
            ArgumentNullException.ThrowIfNull(pts);
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Point(Round(pts[i].X * _m11 + pts[i].Y * _m21 + _dx), Round(pts[i].X * _m12 + pts[i].Y * _m22 + _dy));
        }

        public void TransformVectors(PointF[] pts)
        {
            ArgumentNullException.ThrowIfNull(pts);
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(pts[i].X * _m11 + pts[i].Y * _m21, pts[i].X * _m12 + pts[i].Y * _m22);
        }

        public void TransformVectors(Point[] pts)
        {
            ArgumentNullException.ThrowIfNull(pts);
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Point(Round(pts[i].X * _m11 + pts[i].Y * _m21), Round(pts[i].X * _m12 + pts[i].Y * _m22));
        }

        public void VectorTransformPoints(Point[] pts) => TransformVectors(pts);

        public override bool Equals(object? obj) =>
            obj is Matrix m && m._m11 == _m11 && m._m12 == _m12 && m._m21 == _m21 && m._m22 == _m22 && m._dx == _dx && m._dy == _dy;

        public override int GetHashCode() => base.GetHashCode();

        // this = op * this (Prepend) or this * op (Append), row-vector convention.
        private void Apply(float a11, float a12, float a21, float a22, float adx, float ady, MatrixOrder order)
        {
            float l11, l12, l21, l22, ldx, ldy, r11, r12, r21, r22, rdx, rdy;
            if (order == MatrixOrder.Append)
            {
                l11 = _m11; l12 = _m12; l21 = _m21; l22 = _m22; ldx = _dx; ldy = _dy;
                r11 = a11; r12 = a12; r21 = a21; r22 = a22; rdx = adx; rdy = ady;
            }
            else
            {
                l11 = a11; l12 = a12; l21 = a21; l22 = a22; ldx = adx; ldy = ady;
                r11 = _m11; r12 = _m12; r21 = _m21; r22 = _m22; rdx = _dx; rdy = _dy;
            }
            _m11 = l11 * r11 + l12 * r21;
            _m12 = l11 * r12 + l12 * r22;
            _m21 = l21 * r11 + l22 * r21;
            _m22 = l21 * r12 + l22 * r22;
            _dx = ldx * r11 + ldy * r21 + rdx;
            _dy = ldx * r12 + ldy * r22 + rdy;
        }

        // GDI+'s GpRound.
        private static int Round(float value) => (int)MathF.Floor(value + 0.5f);
    }
}

// ============================================================================
// System.Drawing.Imaging
// ============================================================================

namespace System.Drawing.Imaging
{
    [System.ComponentModel.TypeConverter(typeof(Drawing.ImageFormatConverter))]
    public sealed class ImageFormat
    {
        private readonly Guid _guid;
        public Guid Guid => _guid;
        public ImageFormat(Guid guid) { _guid = guid; }

        public static ImageFormat Bmp => new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Jpeg => new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Png => new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Gif => new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Tiff => new ImageFormat(new Guid("b96b3cb1-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Exif => new ImageFormat(new Guid("b96b3cb2-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Icon => new ImageFormat(new Guid("b96b3cb5-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Emf => new ImageFormat(new Guid("b96b3cac-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Wmf => new ImageFormat(new Guid("b96b3cad-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat MemoryBmp => new ImageFormat(new Guid("b96b3caa-0728-11d3-9d7b-0000f81ef32e"));

        public override bool Equals(object? o) => o is ImageFormat other && _guid == other._guid;
        public override int GetHashCode() => _guid.GetHashCode();
    }

    public sealed class FrameDimension
    {
        private readonly Guid _guid;
        public Guid Guid => _guid;
        public FrameDimension(Guid guid) { _guid = guid; }

        public static FrameDimension Page => new FrameDimension(new Guid("7462dc86-6180-4c7e-8e3f-ee7333a7a483"));
        public static FrameDimension Time => new FrameDimension(new Guid("6aedbd6d-3fb5-418a-83a6-7f45229dc872"));
        public static FrameDimension Resolution => new FrameDimension(new Guid("84236f7b-3bd3-428f-8dab-4ea1439ca315"));

        public override bool Equals(object? o) => o is FrameDimension other && _guid == other._guid;
        public override int GetHashCode() => _guid.GetHashCode();
    }

    public sealed class ImageCodecInfo
    {
        public Guid FormatID { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public string CodecName { get; set; } = string.Empty;
        public static ImageCodecInfo[] GetImageDecoders() => Array.Empty<ImageCodecInfo>();
        public static ImageCodecInfo[] GetImageEncoders() => Array.Empty<ImageCodecInfo>();
    }

    public sealed class Encoder
    {
        private readonly Guid _guid;
        public Guid Guid => _guid;
        public Encoder(Guid guid) { _guid = guid; }
        public static readonly Encoder Quality = new Encoder(new Guid("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb"));
        public static readonly Encoder SaveFlag = new Encoder(new Guid("292266fc-ac40-47bf-8cfc-a85b89a655de"));
        public static readonly Encoder Compression = new Encoder(new Guid("e09d739d-ccd4-44ee-8eba-3fbf8be4fc58"));
    }

    public sealed class EncoderParameter : IDisposable
    {
        public EncoderParameter(Encoder encoder, long value) { }
        public EncoderParameter(Encoder encoder, int value) { }
        public void Dispose() { }
    }

    public sealed class EncoderParameters : IDisposable
    {
        public EncoderParameter[] Param { get; set; }
        public EncoderParameters() { Param = new EncoderParameter[1]; }
        public EncoderParameters(int count) { Param = new EncoderParameter[count]; }
        public void Dispose() { }
    }

    public enum EncoderValue
    {
        MultiFrame = 18, FrameDimensionTime = 21, FrameDimensionResolution = 22,
        FrameDimensionPage = 23, Flush = 20,
    }

    public sealed class PropertyItem
    {
        public int Id { get; set; }
        public int Len { get; set; }
        public short Type { get; set; }
        public byte[]? Value { get; set; }
    }

    public enum PixelFormat
    {
        Format32bppArgb = 2498570, Format24bppRgb = 137224, Format32bppRgb = 139273,
    }
}

// ============================================================================
// System.Drawing.Printing
// ============================================================================

namespace System.Drawing.Printing
{
    public enum PaperSourceKind
    {
        Upper = 1, Lower = 2, Middle = 3, Manual = 4, Envelope = 5,
        EnvelopeManual = 6, AutomaticFeed = 7, TractorFeed = 8,
        SmallFormat = 9, LargeFormat = 10, LargeCapacity = 11,
        Cassette = 14, FormSource = 15, Custom = 257,
    }

    public class PrinterSettings
    {
        public string PrinterName { get; set; } = "";
        public bool IsValid => false;
        public StringCollection InstalledPrinters => new StringCollection();

        public class StringCollection : System.Collections.IEnumerable
        {
            public int Count => 0;
            public string this[int index] => throw new IndexOutOfRangeException();
            public System.Collections.IEnumerator GetEnumerator() { yield break; }
        }
    }
}
