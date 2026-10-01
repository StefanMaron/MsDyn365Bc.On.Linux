using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

internal static class DrawingBridge
{
    private static readonly ConcurrentDictionary<string, MethodInfo> Methods =
        new ConcurrentDictionary<string, MethodInfo>();

    internal static object Dispatch(string signature, object[] values)
    {
        bool fontHeight = signature.StartsWith("System.Drawing.Font::", StringComparison.Ordinal);
        Type type = fontHeight ? typeof(Font) : typeof(Graphics);
        MethodInfo method = Methods.GetOrAdd(signature, key => type.GetMethods()
            .Single(m => type.FullName + "::" + m.Name + "(" +
                string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")" == key));
        object instance = values[0];
        object[] args = values.Skip(1).ToArray();
        Graphics graphics = fontHeight ? (Graphics)args[0] : (Graphics)instance;
        Font font = fontHeight ? (Font)instance : (Font)args[1];
        using (Font scaled = ScaleFont(font, graphics))
        {
            if (scaled != null)
            {
                if (fontHeight) instance = scaled;
                else args[1] = scaled;
            }
            try
            {
                object result = method.Invoke(instance, args);
                Array.Copy(args, 0, values, 1, args.Length);
                return result;
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private static Font ScaleFont(Font font, Graphics graphics)
    {
        if (font == null || graphics == null || font.Unit == GraphicsUnit.Pixel)
            return null; // The original API remains responsible for invalid argument errors.
        float factor;
        switch (font.Unit)
        {
            case GraphicsUnit.Point: factor = graphics.DpiY / 72f; break;
            case GraphicsUnit.Inch: factor = graphics.DpiY; break;
            case GraphicsUnit.Millimeter: factor = graphics.DpiY / 25.4f; break;
            case GraphicsUnit.Document: factor = graphics.DpiY / 300f; break;
            default: throw new NotSupportedException("Unsupported raster font unit: " + font.Unit);
        }
        // libgdiplus ignores Graphics.DpiY for point-font drawing and measurement.
        // Pixel fonts make the physical size explicit without lowering image resolution.
        return new Font(font.FontFamily, font.Size * factor, font.Style,
            GraphicsUnit.Pixel, font.GdiCharSet, font.GdiVerticalFont);
    }
}
