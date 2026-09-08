using System;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.BusinessCentral.Reporting.Common;
using Microsoft.Dynamics.Nav.Types.Data;

internal static class ServiceRender
{
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Run(string[] args)
    {
        if (args.Length < 2 || args.Length > 6)
            throw new ArgumentException("Usage: ServiceRender.exe layout.rdlc output.pdf [rows=3] [compact=false] [dedup=false] [invoice]");
        int rows = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;
        bool compact = args.Length > 3 && bool.Parse(args[3]);
        bool dedup = args.Length > 4 && bool.Parse(args[4]);
        bool invoice = args.Length > 5 && args[5] == "invoice";
        if (args.Length > 5 && !invoice)
            throw new ArgumentException("The only supported extended fixture is invoice.");
        if (rows < 1 || rows > 100000)
            throw new ArgumentOutOfRangeException("rows");
        var dataset = new NavDataSet("DataSet", CultureInfo.GetCultureInfo("en-US"));
        // Choose the same explicit serialization mode on both sides of the RPC.
        typeof(NavDataSet).GetProperty("UseCompactSerialization",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dataset, compact);
        typeof(NavDataSet).GetProperty("UseRowValueDeduplicationCompression").SetValue(dataset, dedup);
        var table = dataset.Tables.Add("Result");
        table.Columns.Add(new NavDataColumn("Description", typeof(string)));
        table.Columns.Add(new NavDataColumn("Quantity", typeof(int)));
        table.Columns.Add(new NavDataColumn("UnitPrice", typeof(decimal)));
        byte[] logo = null;
        if (invoice)
        {
            table.Columns.Add(new NavDataColumn("Logo", typeof(byte[])));
            using (var bitmap = new Bitmap(120, 40))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Color.Orange, 6))
            using (var stream = new MemoryStream())
            {
                graphics.Clear(Color.DarkBlue);
                graphics.DrawLines(pen, new[] { new Point(10, 30), new Point(50, 10), new Point(110, 30) });
                bitmap.Save(stream, ImageFormat.Png);
                logo = stream.ToArray();
            }
        }
        for (int i = 1; i <= rows; i++)
            table.Rows.Add(table.NewRow(invoice
                ? new object[] { string.Format(CultureInfo.InvariantCulture, "Invoice line {0:D3}", i), i, 1.25m, logo }
                : new object[] { "BC dataset row " + i, i, 1.25m }));
        var store = dataset.Serialize();
        bool compressed = (bool)store.GetType().GetProperty("IsCompressed",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
        var roundTrip = new NavDataSet(store, compact, dedup);
        if (roundTrip.Tables[0].Rows.Count != rows)
            throw new InvalidDataException("Original dataset roundtrip lost rows.");
        Console.WriteLine("Original NavDataSet serializer: {0} chunks, {1} bytes, compressed={2}, restored rows={3}, compact={4}, dedup={5}",
            store.Data.Length, store.Data.Sum(c => c.Length), compressed, roundTrip.Tables[0].Rows.Count, compact, dedup);
        byte[] layout = File.ReadAllBytes(args[0]);
        var channel = new Channel("127.0.0.1:5005", ChannelCredentials.Insecure);
        try
        {
            await channel.ConnectAsync(DateTime.UtcNow.AddSeconds(15));
            var client = new ReportingService.ReportingServiceClient(channel);
            var configured = await client.ConfigureServiceAsync(new Configuration
            {
                Settings = "{\"EnableCompactSerialization\":" + compact.ToString().ToLowerInvariant() +
                    ",\"EnableAppDomainIsolation\":false," +
                    "\"EnableStreamingReportDataset\":false,\"ReportAppDomainRecycleCount\":100," +
                    "\"ProhibitedReportServerPrinters\":[],\"OpenTelemetryContextColumns\":\"{}\"," +
                    "\"TraceLevel\":4}"
            }, deadline: DateTime.UtcNow.AddSeconds(30));
            Console.WriteLine("ConfigureService: " + configured);
            if (configured.Exception != null || configured.Error != null)
                throw new InvalidOperationException("ConfigureService failed: " + configured);
            using (var call = client.Render(deadline: DateTime.UtcNow.AddSeconds(120)))
            using (var output = new MemoryStream())
            {
                await call.RequestStream.WriteAsync(new RenderRequest
                {
                    Context = new RenderingContext
                    {
                        Format = "PDF", ReportId = 50123, LayoutId = Guid.NewGuid().ToString(),
                        Culture = 1033, UiCulture = 1033, Timezone = TimeZoneInfo.Utc.ToSerializedString(),
                        EmbedFonts = true, LayoutSize = layout.Length,
                        DatasetSize = store.Data.Sum(c => c.Length), ChunkCount = store.Data.Length,
                        DatasetIsCompressed = compressed, DatasetIsValueDeduplicationCompressed = dedup,
                        ReportParameters = "[]", ReportLabels = "[]"
                    }
                });
                if (!await call.ResponseStream.MoveNext(CancellationToken.None))
                    throw new InvalidDataException("Service did not acknowledge layout cache status.");
                Console.WriteLine("Render handshake: " + call.ResponseStream.Current);
                await call.RequestStream.WriteAsync(new RenderRequest
                {
                    LayoutChunk = new LayoutChunk { Data = ByteString.CopyFrom(layout) }
                });
                foreach (byte[] bytes in store.Data)
                    await call.RequestStream.WriteAsync(new RenderRequest
                    {
                        DatasetChunk = new DatasetChunk { Data = ByteString.CopyFrom(bytes) }
                    });
                await call.RequestStream.CompleteAsync();
                int expectedSize = -1;
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    var response = call.ResponseStream.Current;
                    if (response.Chunk != null)
                    {
                        byte[] chunk = response.Chunk.Data.ToByteArray();
                        output.Write(chunk, 0, chunk.Length);
                        Console.WriteLine("Render artifact chunk: {0} bytes", chunk.Length);
                    }
                    else
                        Console.WriteLine("Render response: " + response);
                    if (response.Exception != null)
                        throw new InvalidOperationException("Service render exception: " + response.Exception);
                    if (response.RenderResult != null && response.RenderResult.Result.Error != null)
                        throw new InvalidOperationException("Service render error: " + response.RenderResult.Result.Error);
                    if (response.Stats != null)
                        expectedSize = response.Stats.ArtifactSize;
                }
                byte[] pdf = output.ToArray();
                if (expectedSize != pdf.Length || pdf.Length < 5 ||
                    System.Text.Encoding.ASCII.GetString(pdf, 0, 5) != "%PDF-")
                    throw new InvalidDataException("Missing, truncated, or non-PDF artifact.");
                File.WriteAllBytes(args[1], pdf);
                Console.WriteLine("Original service gRPC Render produced {0} PDF bytes: {1}", pdf.Length, args[1]);
            }
        }
        finally { await channel.ShutdownAsync(); }
    }
}
