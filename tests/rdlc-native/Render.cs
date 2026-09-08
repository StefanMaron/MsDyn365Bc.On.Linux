using System;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Reporting.WebForms;

internal static class Render
{
    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine("Usage: Render.exe layout.rdlc output.pdf [rows]");
            return 2;
        }
        try
        {
            int rowCount = args.Length == 3 ? int.Parse(args[2]) : 120;
            if (rowCount < 1 || rowCount > 1000000)
                throw new ArgumentOutOfRangeException("rows");
            using (var report = new LocalReport())
            using (var definition = File.OpenRead(args[0]))
            using (var data = new DataTable("DataSet_Result"))
            {
                report.LoadReportDefinition(definition);
                data.Columns.Add("Description", typeof(string));
                data.Columns.Add("Quantity", typeof(int));
                data.Columns.Add("UnitPrice", typeof(decimal));
                data.Columns.Add("Logo", typeof(byte[]));
                byte[] logo;
                using (var image = new Bitmap(120, 40))
                using (var graphics = Graphics.FromImage(image))
                using (var pen = new Pen(Color.Orange, 6))
                using (var bytes = new MemoryStream())
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.DrawLines(pen, new[] { new Point(10, 30), new Point(50, 10), new Point(110, 30) });
                    image.Save(bytes, ImageFormat.Png);
                    logo = bytes.ToArray();
                }
                for (int i = 1; i <= rowCount; i++)
                    data.Rows.Add(string.Format("Invoice line {0:D3}", i), i, 1.25m, logo);
                report.DataSources.Add(new ReportDataSource("DataSet_Result", data));
                string mime, encoding, extension;
                string[] streams;
                Warning[] warnings;
                byte[] pdf = report.Render("PDF", null, out mime, out encoding,
                    out extension, out streams, out warnings);
                if (pdf.Length < 5 || System.Text.Encoding.ASCII.GetString(pdf, 0, 5) != "%PDF-")
                    throw new InvalidDataException("Renderer did not return a PDF.");
                File.WriteAllBytes(args[1], pdf);
                Console.WriteLine("Original ReportViewer rendered {0} bytes, {1}, warnings={2}",
                    pdf.Length, mime, warnings.Length);
                foreach (Warning warning in warnings)
                    Console.WriteLine("{0}: {1}: {2}", warning.Severity, warning.Code, warning.Message);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
