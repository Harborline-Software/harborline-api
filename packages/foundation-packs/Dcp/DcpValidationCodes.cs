namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// Stable, locale-independent DCP validation CODES (ADR 0145 D3.1). Same discipline as
/// <c>PackValidationCodes</c>: the client localizes off the CODE, never the English message. Findings are
/// carried as <c>Harborline.Api.Foundation.Packs.Validation.PackValidationError</c> so the DCP gate and the
/// completeness/PII gate flow ONE error shape to the export outcome + the route.
/// </summary>
public static class DcpValidationCodes
{
    /// <summary>The pack carries no DCP — export fail-closed refuses (ADR 0145 D3.1; S-3 negative gate (i)).</summary>
    public const string Missing = "pack.dcp.missing";

    /// <summary>The DCP version is missing/blank.</summary>
    public const string VersionMissing = "pack.dcp.version.missing";

    /// <summary>The RegulatoryClass is not a defined enum value (defensive — a cast-out-of-range value).</summary>
    public const string RegulatoryClassUndefined = "pack.dcp.regulatory_class.undefined";

    /// <summary>The RegulatoryClass has no cleared counsel row — HARD-BLOCK (ADR 0145 D4 / S-13;
    /// S-3 negative gate (ii)). The single most important DCP refusal: the compensating control for the
    /// solo-collapsed RACI.</summary>
    public const string RegulatoryClassNotCleared = "pack.dcp.regulatory_class.not_cleared";

    /// <summary>The data-sensitivity class is not a defined enum value (defensive).</summary>
    public const string DataSensitivityUndefined = "pack.dcp.data_sensitivity.undefined";

    /// <summary>The DCP declares minors as PRINCIPALS/operators — BLOCKED in v1 absent a counsel pass (D-10).</summary>
    public const string MinorsAsPrincipalsBlocked = "pack.dcp.minors_as_principals.blocked";

    /// <summary>An out-of-scope declaration has a blank scope.</summary>
    public const string OutOfScopeScopeMissing = "pack.dcp.out_of_scope.scope.missing";

    /// <summary>An out-of-scope declaration has a blank counsel disclaimer FK (wording is never free text; D-12).</summary>
    public const string OutOfScopeRefMissing = "pack.dcp.out_of_scope.ref.missing";

    /// <summary>An accountable-individual regime has a blank regime id.</summary>
    public const string RegimeIdMissing = "pack.dcp.regime.id.missing";

    /// <summary>An accountable-individual regime has a blank statutory-basis FK (never free text; D-11).</summary>
    public const string RegimeBasisMissing = "pack.dcp.regime.basis.missing";

    /// <summary>A grace-ladder stage has a blank warning FK (never free text; D-11).</summary>
    public const string RegimeWarningRefMissing = "pack.dcp.regime.warning_ref.missing";

    /// <summary>A declared jurisdictional variance is missing its counsel disclaimer FK (D-13).</summary>
    public const string JurisdictionalVarianceRefMissing = "pack.dcp.jurisdictional_variance.ref.missing";

    /// <summary>The RACI is incomplete (a role is unassigned) — the solo-collapsed default names all four.</summary>
    public const string RaciIncomplete = "pack.dcp.raci.incomplete";
}
