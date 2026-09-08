using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// Default <see cref="IDcpValidator"/> — the fail-closed DCP export gate (ADR 0145 D3.1 / D4). Checks, in
/// order, PRESENCE → SCHEMA well-formedness → COUNSEL-CLEARED <see cref="RegulatoryClass"/>. The
/// counsel-clearance check is the compensating control that stands in for the solo-collapsed vendor RACI
/// (D2) — a non-cleared class is a STRUCTURAL refusal, never a warning.
/// </summary>
public sealed class DcpValidator : IDcpValidator
{
    private readonly IDcpCounselRegister _counselRegister;

    /// <summary>Constructs the validator over the counsel-cleared register (the config the gate reads).</summary>
    public DcpValidator(IDcpCounselRegister counselRegister)
    {
        _counselRegister = counselRegister ?? throw new ArgumentNullException(nameof(counselRegister));
    }

    /// <inheritdoc />
    public IReadOnlyList<PackValidationError> Validate(DomainComplianceProfile? dcp)
    {
        var errors = new List<PackValidationError>();

        // ── Presence (S-3 negative gate (i)) — a pack with no DCP cannot export ────────────────────────
        if (dcp is null)
        {
            errors.Add(new(DcpValidationCodes.Missing, null, "the pack carries no Domain Compliance Profile."));
            return errors;
        }

        // ── Schema: version ────────────────────────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(dcp.DcpVersion))
        {
            errors.Add(new(DcpValidationCodes.VersionMissing, null, "the DCP version is required."));
        }

        // ── RegulatoryClass: defined + COUNSEL-CLEARED (D4 / S-13, HARD-BLOCK) ─────────────────────────
        if (!Enum.IsDefined(dcp.RegulatoryClass))
        {
            errors.Add(new(DcpValidationCodes.RegulatoryClassUndefined, null,
                $"regulatory class '{(int)dcp.RegulatoryClass}' is not a defined value."));
        }
        else if (!_counselRegister.IsCleared(dcp.RegulatoryClass))
        {
            // The single most important refusal: a non-cleared class blocks until its counsel row exists.
            errors.Add(new(DcpValidationCodes.RegulatoryClassNotCleared, dcp.RegulatoryClass.ToString(),
                $"regulatory class '{dcp.RegulatoryClass}' has no cleared counsel row — export is blocked "
                + "(ADR 0145 D4; a counsel row must clear the class before it ships)."));
        }

        // ── DataSensitivity: defined (defensive; re-derived install-side) ──────────────────────────────
        if (!Enum.IsDefined(dcp.DataSensitivity))
        {
            errors.Add(new(DcpValidationCodes.DataSensitivityUndefined, null,
                $"data-sensitivity class '{(int)dcp.DataSensitivity}' is not a defined value."));
        }

        // ── Audience: minors-as-principals is BLOCKED v1 (counsel D-10, no path exists) ────────────────
        if (dcp.Audience is { MinorsAsPrincipals: true })
        {
            errors.Add(new(DcpValidationCodes.MinorsAsPrincipalsBlocked, null,
                "minors-as-principals requires a counsel pass + ONR follow-up before any pack may set it (D-10)."));
        }

        // ── Out-of-scope: scope + counsel FK non-blank (wording is never free text, D-12) ──────────────
        foreach (var declaration in dcp.OutOfScope ?? Array.Empty<DcpOutOfScopeDeclaration>())
        {
            if (string.IsNullOrWhiteSpace(declaration.Scope))
            {
                errors.Add(new(DcpValidationCodes.OutOfScopeScopeMissing, null,
                    "an out-of-scope declaration has a blank scope."));
            }
            if (string.IsNullOrWhiteSpace(declaration.CounselDisclaimerRef))
            {
                errors.Add(new(DcpValidationCodes.OutOfScopeRefMissing, declaration.Scope,
                    "an out-of-scope declaration must reference a counsel disclaimer (never free text, D-12)."));
            }
        }

        // ── Accountable-individual regimes: id + statutory-basis FK + warning FKs (D-11) ───────────────
        foreach (var regime in dcp.AccountableIndividualRegimes ?? Array.Empty<AccountableIndividualRegime>())
        {
            if (string.IsNullOrWhiteSpace(regime.RegimeId))
            {
                errors.Add(new(DcpValidationCodes.RegimeIdMissing, null,
                    "an accountable-individual regime has a blank regime id."));
            }
            if (string.IsNullOrWhiteSpace(regime.StatutoryBasisRef))
            {
                errors.Add(new(DcpValidationCodes.RegimeBasisMissing, regime.RegimeId,
                    "an accountable-individual regime must reference a statutory basis (never free text, D-11)."));
            }
            foreach (var stage in regime.GracePolicy?.Stages ?? Array.Empty<GraceLadderStage>())
            {
                if (string.IsNullOrWhiteSpace(stage.WarningRef))
                {
                    errors.Add(new(DcpValidationCodes.RegimeWarningRefMissing, regime.RegimeId,
                        "a grace-ladder stage must reference a counsel notice (never free text, D-11)."));
                }
            }
        }

        // ── Jurisdictional variance: a declared variance carries a counsel FK (D-13) ───────────────────
        if (dcp.JurisdictionalVariance is { HasVariance: true } variance
            && string.IsNullOrWhiteSpace(variance.CounselDisclaimerRef))
        {
            errors.Add(new(DcpValidationCodes.JurisdictionalVarianceRefMissing, null,
                "a declared jurisdictional variance must reference a counsel disclaimer (D-13)."));
        }

        // ── RACI: all four roles assigned (the solo-collapsed default names all four; D2) ──────────────
        var raci = dcp.Raci;
        if (raci is null
            || string.IsNullOrWhiteSpace(raci.Designer) || string.IsNullOrWhiteSpace(raci.Arbiter)
            || string.IsNullOrWhiteSpace(raci.Steward) || string.IsNullOrWhiteSpace(raci.Curator))
        {
            errors.Add(new(DcpValidationCodes.RaciIncomplete, null,
                "the DCP RACI must assign all four roles (Designer/Arbiter/Steward/Curator)."));
        }

        return errors;
    }
}
