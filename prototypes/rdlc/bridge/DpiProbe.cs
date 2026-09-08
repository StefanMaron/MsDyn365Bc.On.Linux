using System;
using System.Drawing;
internal static class DpiProbe
{
    private static void Main()
    {
        foreach (float dpi in new[] { 96f, 300f })
        using (var bitmap = new Bitmap(1800, 1200))
        using (var font = new Font("Liberation Sans", 16, FontStyle.Regular, GraphicsUnit.Point))
        {
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
                Console.WriteLine("requested={0}, graphics={1}, height={2}, measured={3}, explicit={4}",
                    dpi, graphics.DpiY, font.GetHeight(graphics),
                    graphics.MeasureString("Native chart", font), font.GetHeight(dpi));
        }
    }
}
