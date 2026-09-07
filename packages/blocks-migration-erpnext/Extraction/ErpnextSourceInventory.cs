using System.Collections.Generic;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// One DocType present in the source, with its census classification and source
/// row count (ADR 0100 C5 / the migration report's DocType census). Content-free
/// — carries only the DocType NAME and a COUNT, never any record contents (C9).
/// </summary>
/// <param name="DocType">The ERPNext DocType name (the <c>tab*</c> table with the prefix stripped).</param>
/// <param name="Classification">Whether this DocType is mapped, known-irrelevant, or unmapped-unknown.</param>
/// <param name="SourceRowCount">The number of source rows of this DocType present in the dump.</param>
public sealed record ErpnextDocTypeCensusEntry(
    string DocType,
    ErpnextDocTypeClassification Classification,
    int SourceRowCount);

/// <summary>
/// The census classification of a DocType found in the source (ADR 0100 C5).
/// </summary>
public enum ErpnextDocTypeClassification
{
    /// <summary>In <see cref="ErpnextDocTypeMap"/> — extracted into an <c>Erpnext*Source</c> DTO.</summary>
    Mapped,

    /// <summary>
    /// On the framework/system ignore allowlist (e.g. <c>tabDocType</c>, <c>tabSeries</c>,
    /// session/permission tables) — deliberately ignored; NOT financial-or-business data.
    /// </summary>
    KnownIrrelevant,

    /// <summary>
    /// Present in the dump, business/financial-looking, NOT in the map and NOT on the
    /// ignore allowlist. COUNTED and LISTED in the report's <c>_unmapped/</c> section
    /// for CIC review — never an error, never silently dropped (this is how a custom
    /// <c>tabProperty</c>/<c>tabLease</c> would surface).
    /// </summary>
    UnmappedUnknown,
}

/// <summary>
/// The full source-inventory census produced by
/// <see cref="IErpnextSourceExtractor.ReadInventoryAsync"/> — every <c>tab*</c>
/// DocType in the source partitioned into mapped / known-irrelevant /
/// unmapped-unknown, plus the run-provenance source mode (ADR 0100 C5 / C6).
/// </summary>
/// <remarks>
/// Content-free (C9): stores DocType names + counts + a mode descriptor only.
/// The <see cref="UnmappedUnknown"/> collection IS the report's <c>_unmapped/</c>
/// section input — a non-empty list is VISIBLE to CIC, not an error.
/// </remarks>
public sealed class ErpnextSourceInventory
{
    /// <summary>Every DocType in the source, with its classification + count.</summary>
    public IReadOnlyList<ErpnextDocTypeCensusEntry> Entries { get; }

    /// <summary>
    /// The access mode that produced this run (v1 = <see cref="SourceAccessMode.MariaDbDump"/>),
    /// recorded by the orchestrator in the migration report (C6 forward-hook).
    /// </summary>
    public SourceAccessMode SourceMode { get; }

    /// <summary>Initializes the inventory from its census entries + the source mode.</summary>
    public ErpnextSourceInventory(IReadOnlyList<ErpnextDocTypeCensusEntry> entries, SourceAccessMode sourceMode)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
        SourceMode = sourceMode;
    }

    /// <summary>The mapped DocTypes (extracted into DTOs).</summary>
    public IEnumerable<ErpnextDocTypeCensusEntry> Mapped =>
        Entries.Where(e => e.Classification == ErpnextDocTypeClassification.Mapped);

    /// <summary>The known-irrelevant DocTypes (deliberately ignored).</summary>
    public IEnumerable<ErpnextDocTypeCensusEntry> KnownIrrelevant =>
        Entries.Where(e => e.Classification == ErpnextDocTypeClassification.KnownIrrelevant);

    /// <summary>
    /// The unmapped-unknown DocTypes — the report's <c>_unmapped/</c> section. A
    /// non-empty result is for CIC review, never an extraction error (ADR 0100 C5).
    /// </summary>
    public IEnumerable<ErpnextDocTypeCensusEntry> UnmappedUnknown =>
        Entries.Where(e => e.Classification == ErpnextDocTypeClassification.UnmappedUnknown);

    /// <summary>Count of unmapped-unknown DocTypes (the <c>_unmapped/</c> headline number).</summary>
    public int UnmappedUnknownCount => UnmappedUnknown.Count();
}
