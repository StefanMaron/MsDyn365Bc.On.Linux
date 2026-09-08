using System;
using System.Drawing;

internal static class DrawingBridgeTests
{
    private static int Main()
    {
        float referenceWidth = 0;
        foreach (float dpi in new[] { 96f, 300f })
        using (var bitmap = new Bitmap(1800, 1200))
        using (var font = new Font("Liberation Sans", 16, FontStyle.Bold, GraphicsUnit.Point))
        {
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var measured = (SizeF)Bridge.Dispatch(
                    "drawing:System.Drawing.Graphics::MeasureString(System.String,System.Drawing.Font)",
                    null, new object[] { graphics, "Native chart", font });
                float height = (float)Bridge.Dispatch(
                    "drawing:System.Drawing.Font::GetHeight(System.Drawing.Graphics)",
                    null, new object[] { font, graphics });
                float expectedHeight = font.GetHeight(dpi);
                if (Math.Abs(height - expectedHeight) > 0.1f)
                    throw new Exception("Raster font height does not match the requested physical DPI.");
                if (dpi == 96) referenceWidth = measured.Width;
                else if (Math.Abs(measured.Width / referenceWidth - dpi / 96) > 0.1f)
                    throw new Exception("Raster text width does not scale with DPI.");
                graphics.Clear(Color.White);
                Bridge.Dispatch(
                    "drawing:System.Drawing.Graphics::DrawString(System.String,System.Drawing.Font,System.Drawing.Brush,System.Single,System.Single)",
                    null, new object[] { graphics, "Native chart", font, Brushes.Black, 0f, 0f });
                int lastInk = 0;
                for (int x = 0; x < bitmap.Width; x++)
                for (int y = 0; y < Math.Ceiling(measured.Height) + 2; y++)
                    if (bitmap.GetPixel(x, y).R < 128) lastInk = Math.Max(lastInk, x);
                if (lastInk < measured.Width * 0.85 || lastInk > measured.Width + 2)
                    throw new Exception("Raster drawing and text measurement disagree.");
                Console.WriteLine("DPI {0}: measured={1}, height={2}, last ink={3}",
                    dpi, measured, height, lastInk);
            }
        }
        return 0;
    }
}
