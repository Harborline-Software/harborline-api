using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// A free-form dimensional tag — the second analytical dimension on a journal
/// line (<see cref="JournalEntryLine.ClassId"/>) alongside
/// <see cref="JournalEntryLine.PropertyId"/>. Used as the fallback target of the
/// ERPNext cost-center heuristic (migration-importer spec §3.4): a cost-center
/// that resolves to neither a custom Property DocType nor an alias-map entry is
/// preserved verbatim as a <see cref="Classification"/> so no dimensional data is
/// lost during migration.
/// </summary>
/// <param name="Id">Unique classification identifier.</param>
/// <param name="Name">
/// Human-readable name — for an imported cost-center this is the ERPNext
/// <c>cost_center_name</c> preserved verbatim.
/// </param>
/// <param name="ExternalRef">
/// Optional external-system reference (e.g. the ERPNext cost-center
/// <c>name</c> natural key) for import idempotency + trace-back.
/// </param>
/// <param name="IsActive">Soft-delete flag.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
public sealed record Classification(
    ClassificationId Id,
    string Name,
    string? ExternalRef = null,
    bool IsActive = true,
    Instant? CreatedAtUtc = null)
{
    /// <summary>
    /// Build a well-formed <see cref="Classification"/> with a generated id and a
    /// creation timestamp supplied by the admitting act.
    /// </summary>
    public static Classification Create(string name, Instant createdAtUtc, string? externalRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Classification(
            Id: ClassificationId.NewId(),
            Name: name,
            ExternalRef: externalRef,
            IsActive: true,
            CreatedAtUtc: createdAtUtc);
    }
}
