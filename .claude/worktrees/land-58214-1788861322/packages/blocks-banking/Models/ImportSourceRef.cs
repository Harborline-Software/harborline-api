namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// Provenance reference identifying which import batch or feed connection
/// produced a <see cref="StatementLine"/>.
/// Per ADR 0112 Part 1 §2 — statement-line entity, Source field.
/// </summary>
/// <param name="Kind">Whether this line came from a file import or a live feed connection.</param>
/// <param name="BatchId">
/// For file imports: the import batch identifier (opaque, typically a GUID).
/// For feed imports: the feed connection id.
/// </param>
/// <param name="OrdinalWithinBatch">
/// The 0-based position of this line within its import batch.
/// Used as part of the hash-dedup key for file lines so two genuinely-identical
/// lines within one statement (e.g. two $20 ATM withdrawals) are both preserved
/// (ADR 0112 fin-acct N1).
/// </param>
public readonly record struct ImportSourceRef(
    ImportSourceKind Kind,
    string BatchId,
    int OrdinalWithinBatch);

/// <summary>
/// Whether a <see cref="StatementLine"/> originated from a file import or a live feed.
/// </summary>
public enum ImportSourceKind
{
    /// <summary>Line was parsed from an operator-uploaded file (CSV/OFX/QIF/CAMT.053).</summary>
    FileImport,

    /// <summary>Line was pulled via a live <see cref="Feed.IBankFeedProvider"/> connection.</summary>
    LiveFeed,
}
