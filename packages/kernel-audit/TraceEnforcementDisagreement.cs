using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Harborline.Api.Kernel.Audit;

internal sealed class TraceEnforcementDisagreement : IDisposable
{
    private static readonly ActivitySource Source = new("Harborline.Kernel.Audit");
    private static readonly TraceEnforcementDisagreement Instance = new();
    private readonly Meter _meter = new("Harborline.Kernel.Audit");
    private long _total;

    internal TraceEnforcementDisagreement() => _meter.CreateObservableCounter(
        "authorization.trace.disagreements", () => Interlocked.Read(ref _total));

    // Register the observable zero before the audit trail accepts any append.
    internal static void Initialize() => _ = Instance;

    internal static void Record(string code)
    {
        Interlocked.Increment(ref Instance._total);
        using var activity = Source.StartActivity("authorization.trace.disagreement");
        var span = activity ?? Activity.Current;
        span?.SetStatus(ActivityStatusCode.Error);
        span?.SetTag("error.type", code);
    }

    public void Dispose() => _meter.Dispose();
}
