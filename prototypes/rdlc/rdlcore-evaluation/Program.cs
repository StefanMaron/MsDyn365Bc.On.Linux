using System.Data;
using Microsoft.Reporting.NETCore;
using SkiaSharp;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: NativeRdlcProbe <layout.rdlc> <output.pdf>");
    return 2;
}

using var report = new LocalReport();
using var definition = File.OpenRead(args[0]);
report.LoadReportDefinition(definition);
var data = new DataTable("DataSet_Result");
data.Columns.Add("Description", typeof(string));
data.Columns.Add("Quantity", typeof(int));
data.Columns.Add("UnitPrice", typeof(decimal));
data.Columns.Add("Logo", typeof(byte[]));
using var bitmap = new SKBitmap(120, 40);
using (var canvas = new SKCanvas(bitmap))
{
    canvas.Clear(SKColors.DarkBlue);
    using var paint = new SKPaint { Color = SKColors.Orange, StrokeWidth = 6 };
    canvas.DrawLine(10, 30, 50, 10, paint);
    canvas.DrawLine(50, 10, 110, 30, paint);
}
using var image = SKImage.FromBitmap(bitmap);
using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
byte[] logo = encoded.ToArray();
for (int i = 1; i <= 120; i++)
    data.Rows.Add($"Invoice line {i:D3}", i, 1.25m, logo);
report.DataSources.Add(new ReportDataSource("DataSet_Result", data));
byte[] pdf = report.Render("PDF", null, out string mime, out string encoding,
    out string extension, out string[] streams, out Warning[] warnings);
if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
    throw new InvalidDataException("Renderer returned an invalid PDF header.");
File.WriteAllBytes(args[1], pdf);
Console.WriteLine($"Rendered {pdf.Length} bytes, MIME {mime}, warnings {warnings.Length}");
foreach (Warning warning in warnings)
    Console.WriteLine($"{warning.Severity}: {warning.Code}: {warning.Message}");
return 0;
