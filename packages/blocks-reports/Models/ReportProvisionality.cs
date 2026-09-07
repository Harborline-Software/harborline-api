namespace Harborline.Api.Blocks.Reports;

/// <summary>
/// Provisionality envelope attached to a
/// <see cref="ReportRunResult{TResult}"/> when one or more upstream
/// fiscal periods were not yet in a <c>Locked</c> state at run time.
/// Cartridge results that implement
/// <see cref="IReportProvisionalitySource"/> surface this
/// information; the runner extracts it and propagates it through the
/// result envelope.
/// </summary>
/// <param name="IsProvisional">
/// True when the result derives from data that crosses an
/// <c>Open</c> or <c>SoftClosed</c> accounting period. Consumers
/// should display a prominent "provisional — values may shift on
/// period close" warning when this flag is set.
/// </param>
/// <param name="Warnings">
/// Human-readable explanations for why the result is provisional,
/// e.g., "Period 2026-05 is SoftClosed; debits posted after
/// soft-close are included." At most
/// <see cref="ReportRunnerOptions.MaxWarnings"/> entries are
/// propagated; additional warnings are silently truncated.
/// </param>
public sealed record ReportProvisionality(
    bool IsProvisional,
    System.Collections.Generic.IReadOnlyList<string> Warnings)
{
    /// <summary>Non-provisional result with no warnings.</summary>
    public static readonly ReportProvisionality None =
        new(false, System.Array.Empty<string>());
}
