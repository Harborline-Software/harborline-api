namespace Harborline.Api.Foundation.Import.Extraction;

/// <summary>
/// Describes which extraction access mode produced an import run, for
/// migration-report provenance (ADR 0100 C6 forward-hook).
/// </summary>
/// <remarks>
/// v1 ships exactly ONE mode — <see cref="MariaDbDump"/> — per ADR 0100 C6
/// (security-engineering amendment A; collapse to dump-only so the C4 read-only
/// posture is uniformly provable). <see cref="RestApi"/> and <see cref="DocTypeCsv"/>
/// are reserved, NOT-blessed future modes deferred behind this seam; adding one
/// later is the additive operation the seam exists for. The orchestrator records
/// the mode that produced a run so per-mode differences (e.g. credential handling,
/// C9) are visible in the report — it never silently substitutes a mode.
/// </remarks>
public enum SourceAccessMode
{
    /// <summary>
    /// v1's SOLE blessed mode: a static MariaDB dump (offline, deterministic,
    /// re-runnable, read-only, no live-Frappe coupling). ADR 0100 C6.
    /// </summary>
    MariaDbDump,

    /// <summary>
    /// RESERVED / deferred (NOT blessed in v1). ERPNext REST API. Reintroduces the
    /// live-Frappe coupling spec §1.2 excludes and has no SQL-level read-only
    /// guarantee — deferred behind this seam pending a future ADR amendment.
    /// </summary>
    RestApi,

    /// <summary>
    /// RESERVED / deferred (NOT blessed in v1). Per-DocType CSV/JSON export
    /// (the Stage-03 spec's original assumption) — deferred behind this seam.
    /// </summary>
    DocTypeCsv,
}
