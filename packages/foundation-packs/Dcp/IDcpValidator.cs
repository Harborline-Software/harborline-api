using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// The DCP export gate (ADR 0145 D3.1). Validates a <see cref="DomainComplianceProfile"/> for PRESENCE +
/// SCHEMA + COUNSEL-CLEARED enum values at export. Fail-closed: any finding refuses the export (no
/// install-anyway path, S-13). Emits stable localizable codes (<see cref="DcpValidationCodes"/>) as
/// <see cref="PackValidationError"/> so the DCP gate and the completeness/PII gate share one error shape.
/// </summary>
/// <remarks>
/// This gate validates the DECLARED DCP only. The classification-bearing claims (RegulatoryClass,
/// DataSensitivity) are RE-DERIVED at INSTALL against the platform's own ADR 0128/0139 registries
/// (D3.2 / 0143 F2 / S-9) — that re-derivation is a Phase-1 STUB (install-side, not built here).
/// </remarks>
public interface IDcpValidator
{
    /// <summary>
    /// Validates <paramref name="dcp"/> at the export gate. Returns EVERY finding (all checks run — the
    /// caller sees the full picture, not just the first error); an empty list means the DCP passes the
    /// gate. A <c>null</c> <paramref name="dcp"/> is the "no DCP" refusal (<see cref="DcpValidationCodes.Missing"/>).
    /// </summary>
    IReadOnlyList<PackValidationError> Validate(DomainComplianceProfile? dcp);
}
