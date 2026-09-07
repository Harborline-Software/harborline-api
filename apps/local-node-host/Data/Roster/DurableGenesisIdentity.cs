using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>Reads the install's signed genesis before the host can publish any roster or transport state.</summary>
public static class DurableGenesisIdentity
{
    /// <summary>Stable startup refusal when the install's derived signing identity disagrees with its log.</summary>
    public const string MismatchCode = GenesisStartupMessages.MismatchCode;

    /// <summary>Stable startup refusal when an existing log cannot supply one verified genesis.</summary>
    public const string InvalidLogCode = GenesisStartupMessages.InvalidLogCode;

    /// <summary>Returns null when this tenant has no roster log; never repairs or replaces a stored root.</summary>
    public static async Task<MemberRoster?> ReadAsync(
        IDbContextFactory<NodeLocalRosterDbContext> factory,
        Guid tenantId,
        PrincipalId derivedPrincipal,
        IOperationVerifier verifier,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        var canonicalTeam = tenantId.ToString("D");
        if (!await db.RosterRecords.AsNoTracking()
                .Where(row => row.TeamId == canonicalTeam).AnyAsync(ct).ConfigureAwait(false))
            return null;

        MemberRoster stored;
        try
        {
            stored = await new VerifiedTenantRosterReader(factory, verifier)
                .ReadPartialAsync(new TenantId(canonicalTeam), derivedPrincipal, ct).ConfigureAwait(false);
        }
        catch (VerifiedTenantRosterRefusedException ex)
        {
            var admissions = await db.RosterRecords.AsNoTracking()
                .Where(row => row.TeamId == canonicalTeam && row.Kind == (int)RosterRecordKind.Admission)
                .ToListAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                admissions.Any(row => IsRejectedLegacyAdmission(row, tenantId, verifier))
                    ? GenesisStartupMessages.LegacyFormat
                    : GenesisStartupMessages.InvalidLog, ex);
        }

        // Compare the full root-derived key, never the mutable OS account label or a short key prefix.
        // An enrolled node belongs to the verified chain without being its founder.
        if (!stored.Members.Any(member => member.PublicKey.Equals(derivedPrincipal)))
            throw new InvalidOperationException(
                stored.EnumerateAdmissions().Any(member => member.PublicKey.Equals(derivedPrincipal))
                    ? GenesisStartupMessages.Removed
                    : GenesisStartupMessages.Mismatch);
        return stored;
    }

    // Diagnostic only, after the authoritative reader has refused. A legacy signature never grants trust.
    private static bool IsRejectedLegacyAdmission(NodeRosterRecord row, Guid tenantId, IOperationVerifier verifier)
    {
        var admission = NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull();
        if (admission is null || RosterSigning.VerifyAdmission(
                tenantId, admission.PartyId, admission.PublicKey, admission.Admission, verifier))
            return false;

        var signed = admission.Admission;
        try
        {
            var payload = new LegacyAdmissionPayload(row.TeamId, admission.PartyId,
                admission.PublicKey.ToBase64Url(), signed.AdmittedByPartyId, signed.AdmittedByPublicKey,
                signed.IsGenesis, signed.DmPublicKey ?? string.Empty, signed.XWingPublicKey ?? string.Empty,
                signed.AdmittedViaTokenId ?? string.Empty, signed.MintingSessionEvidence ?? string.Empty);
            return new Ed25519Verifier().Verify(new SignedOperation<LegacyAdmissionPayload>(payload,
                PrincipalId.FromBase64Url(signed.AdmittedByPublicKey), signed.IssuedAt, signed.Nonce,
                Signature.FromBase64Url(signed.Signature)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Frozen version-1 envelope, deliberately lacking 291's FormatVersion and AdmittedPermissions.
    private sealed record LegacyAdmissionPayload(
        string TeamId, string AdmittedPartyId, string AdmittedPublicKey,
        string AdmittedByPartyId, string AdmittedByPublicKey, bool IsGenesis,
        string AdmittedDmPublicKey, string AdmittedXWingPublicKey,
        string AdmittedViaTokenId, string AdmittedUnderSessionEvidence);
}
