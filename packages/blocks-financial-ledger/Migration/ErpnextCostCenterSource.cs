namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// The parsed in-memory ERPNext "Cost Center" DocType — the source record the
/// <see cref="IErpnextCostCenterImporter"/> consumes (migration-importer spec
/// §3.4). Access-mode-agnostic: just a typed DTO; the
/// <c>Harborline.Api.Foundation.Import.Extraction.ISourceReader</c> seam maps a raw
/// <c>SourceRow</c> into this shape before the upsert pass runs.
/// </summary>
/// <param name="Name">
/// ERPNext stable id (the <c>name</c> field) — the natural key used for
/// idempotency + trace-back (stored as the resolved entity's <c>ExternalRef</c>).
/// </param>
/// <param name="Modified">
/// ERPNext <c>modified</c> timestamp string — opaque version key (ISO-8601;
/// lexicographic order matches temporal order).
/// </param>
/// <param name="CostCenterName">
/// Display name (<c>cost_center_name</c>). ERPNext frequently abuses this as a
/// property identifier when the Frappe property module is not installed — the
/// §3.4 heuristic resolves it to a known Property or preserves it verbatim as a
/// <see cref="Harborline.Api.Blocks.FinancialLedger.Models.Classification"/>.
/// </param>
/// <param name="ParentCostCenterName">
/// Optional parent cost-center (by ERPNext name) — group cost-centers form a
/// hierarchy just like accounts. <see langword="null"/> for top-level nodes.
/// </param>
/// <param name="IsGroup">If true → a non-leaf grouping node, not a postable dimension.</param>
/// <param name="Disabled">If true → maps to <c>IsActive = false</c>.</param>
public sealed record ErpnextCostCenterSource(
    string Name,
    string Modified,
    string CostCenterName,
    string? ParentCostCenterName = null,
    bool IsGroup = false,
    bool Disabled = false);
