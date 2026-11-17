using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

using Harborline.Api.LocalNodeHost.Tests.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

internal sealed class TelemetrySentinelFence : IDisposable
{
    internal sealed record Hit(string Signal, string Source, string Location);
    private sealed record Value(Hit Location, string? Text);
    private sealed record Payload(long Arrival, string Path, byte[] Bytes);

    private readonly ConcurrentDictionary<string, LogCapture> logs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Value> values = new();
    private readonly ConcurrentQueue<Payload> payloads = new();
    private readonly ActivityListener activities;
    private readonly MeterListener measurements = new();
    private long arrivals;

    internal TelemetrySentinelFence()
    {
        activities = new ActivityListener
        {
            ShouldListenTo = source => IsApplication(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = CaptureActivity,
        };
        ActivitySource.AddActivityListener(activities);
        measurements.InstrumentPublished = (instrument, listener) =>
        {
            if (IsApplication(instrument.Meter.Name)) listener.EnableMeasurementEvents(instrument);
        };
        measurements.SetMeasurementEventCallback<byte>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<short>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<int>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<long>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<float>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<double>(CaptureMeasurement);
        measurements.SetMeasurementEventCallback<decimal>(CaptureMeasurement);
        measurements.Start();
    }

    internal long ExportPosition => Interlocked.Read(ref arrivals);
    internal bool HasLogs => logs.Values.Any(capture => capture.Records.Count > 0);
    internal bool HasPayloadAfter(string path, long position) =>
        payloads.Any(payload => payload.Arrival > position && payload.Path == path && payload.Bytes.Length > 0);

    internal ILoggerProvider LogProvider => new Provider(this);

    private ILogger CreateLogger(string categoryName) =>
        logs.GetOrAdd(categoryName, _ => new LogCapture()).CreateLogger(categoryName);

    // The fixture owns the listeners across the authorization-seeding host restart.
    private sealed class Provider(TelemetrySentinelFence owner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => owner.CreateLogger(categoryName);
        public void Dispose() { }
    }

    internal async Task ReceiveAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        payloads.Enqueue(new Payload(Interlocked.Increment(ref arrivals), context.Request.Path, body.ToArray()));
        context.Response.ContentType = "application/x-protobuf";
    }

    internal Hit[] Find(params string[] sentinels)
    {
        bool Contains(string? text) => text is not null &&
            sentinels.Any(sentinel => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase));
        string Redact(string text)
        {
            // A dynamically built template or key could itself contain surrounding tenant values.
            return Contains(text) ? "[sentinel in location]" : text;
        }

        var hits = new List<Hit>();
        foreach (var (category, capture) in logs)
        {
            foreach (var record in capture.Records)
            {
                if (Contains(category) || Contains(record.Message) ||
                    record.Values.Any(pair => Contains(pair.Key) || Contains(pair.Value?.ToString())))
                {
                    // One location per log call, even when both the rendered line and arguments contain it.
                    var template = record.Value("{OriginalFormat}")?.ToString() ?? "rendered message";
                    hits.Add(new Hit("log", category, template));
                }
            }
        }
        hits.AddRange(values.Where(value => Contains(value.Text) || Contains(value.Location.Source) ||
            Contains(value.Location.Location)).Select(value => value.Location));
        foreach (var payload in payloads)
        {
            if (sentinels.Any(sentinel => ContainsAscii(payload.Bytes, Encoding.UTF8.GetBytes(sentinel))))
                hits.Add(new Hit(payload.Path == "/v1/metrics" ? "exported metric" : "exported trace",
                    "OTLP", payload.Path));
        }
        return hits.Select(hit => new Hit(Redact(hit.Signal), Redact(hit.Source), Redact(hit.Location)))
            .Distinct().OrderBy(hit => hit.Signal, StringComparer.Ordinal)
            .ThenBy(hit => hit.Source, StringComparer.Ordinal)
            .ThenBy(hit => hit.Location, StringComparer.Ordinal).ToArray();
    }

    private static bool IsApplication(string name) =>
        !name.StartsWith("Microsoft.", StringComparison.Ordinal) && !name.StartsWith("System.", StringComparison.Ordinal);

    private void Capture(string signal, string source, string location, object? value) =>
        values.Enqueue(new Value(new Hit(signal, source, location), value?.ToString()));

    private void CaptureActivity(Activity activity)
    {
        var source = activity.Source.Name;
        Capture("activity", source, "operation name", activity.OperationName);
        Capture("activity", source, "display name", activity.DisplayName);
        Capture("activity", source, "status description", activity.StatusDescription);
        Capture("activity", source, "trace state", activity.TraceStateString);
        foreach (var tag in activity.TagObjects) Capture("activity", source, tag.Key, tag.Value);
        foreach (var baggage in activity.Baggage) Capture("activity", source, "baggage:" + baggage.Key, baggage.Value);
        foreach (var entry in activity.Events)
        {
            Capture("activity", source, "event name", entry.Name);
            foreach (var tag in entry.Tags) Capture("activity", source, "event:" + tag.Key, tag.Value);
        }
        foreach (var link in activity.Links)
        {
            Capture("activity", source, "link trace state", link.Context.TraceState);
            if (link.Tags is not null)
                foreach (var tag in link.Tags) Capture("activity", source, "link:" + tag.Key, tag.Value);
        }
    }

    private void CaptureMeasurement<T>(Instrument instrument, T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) where T : struct
    {
        Capture("measurement", instrument.Meter.Name, "instrument name", instrument.Name);
        foreach (var tag in tags) Capture("measurement", instrument.Meter.Name, tag.Key, tag.Value);
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> sentinel)
    {
        for (var start = 0; start <= payload.Length - sentinel.Length; start++)
        {
            var matched = 0;
            while (matched < sentinel.Length && Lower(payload[start + matched]) == Lower(sentinel[matched])) matched++;
            if (matched == sentinel.Length) return true;
        }
        return false;

        static byte Lower(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }

    public void Dispose()
    {
        measurements.Dispose();
        activities.Dispose();
        foreach (var capture in logs.Values) capture.Dispose();
    }
}
