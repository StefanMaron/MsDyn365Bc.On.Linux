// Stub replacement for OpenTelemetry.Exporter.Geneva.dll
// Provides no-op implementations of the Geneva exporter types so the BC service
// tier doesn't crash on Linux (where ETW is not available).

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Exporter.Geneva
{
    public class GenevaExporterOptions
    {
        public string? ConnectionString { get; set; }
        public IEnumerable<string>? CustomFields { get; set; }
        public ExceptionStackExportMode ExceptionStackExportMode { get; set; }
        public EventNameExportMode EventNameExportMode { get; set; }
        public bool IncludeTraceStateForSpan { get; set; }
        public IReadOnlyDictionary<string, string>? TableNameMappings { get; set; }
        public IReadOnlyDictionary<string, object>? PrepopulatedFields { get; set; }
    }

    public class GenevaMetricExporterOptions
    {
        public string? ConnectionString { get; set; }
        public int MetricExportIntervalMilliseconds { get; set; } = 60000;
        public IReadOnlyDictionary<string, object>? PrepopulatedMetricDimensions { get; set; }
    }

    public enum ExceptionStackExportMode { Drop, ExportAsString }

    [Flags]
    public enum EventNameExportMode { None = 0, ExportAsPartAName = 1 }

    // Stub exporters — no-op
    public class GenevaLogExporter : BaseExporter<LogRecord>
    {
        public GenevaLogExporter(GenevaExporterOptions options) { }
        public override ExportResult Export(in Batch<LogRecord> batch) => ExportResult.Success;
    }

    public class GenevaTraceExporter : BaseExporter<System.Diagnostics.Activity>
    {
        public GenevaTraceExporter(GenevaExporterOptions options) { }
        public override ExportResult Export(in Batch<System.Diagnostics.Activity> batch) => ExportResult.Success;
    }

    // Extension methods matching the original API
    public static class GenevaExporterHelperExtensions
    {
        public static TracerProviderBuilder AddGenevaTraceExporter(this TracerProviderBuilder builder)
            => builder;
        public static TracerProviderBuilder AddGenevaTraceExporter(this TracerProviderBuilder builder, Action<GenevaExporterOptions>? configure)
            => builder;
        public static TracerProviderBuilder AddGenevaTraceExporter(this TracerProviderBuilder builder, string? name, Action<GenevaExporterOptions>? configure)
            => builder;
    }

    public static class GenevaMetricExporterExtensions
    {
        // MeterProviderBuilder might not be available without the Metrics package.
        // BC may or may not use metrics — stub only if needed.
    }
}

namespace Microsoft.Extensions.Logging
{
    public static class GenevaLoggingExtensions
    {
        public static OpenTelemetryLoggerOptions AddGenevaLogExporter(
            this OpenTelemetryLoggerOptions options, Action<OpenTelemetry.Exporter.Geneva.GenevaExporterOptions>? configure)
            => options;

        public static LoggerProviderBuilder AddGenevaLogExporter(
            this LoggerProviderBuilder builder)
            => builder;
        public static LoggerProviderBuilder AddGenevaLogExporter(
            this LoggerProviderBuilder builder, Action<OpenTelemetry.Exporter.Geneva.GenevaExporterOptions>? configureExporter)
            => builder;
        public static LoggerProviderBuilder AddGenevaLogExporter(
            this LoggerProviderBuilder builder, string? name, Action<OpenTelemetry.Exporter.Geneva.GenevaExporterOptions>? configureExporter)
            => builder;
    }
}
