using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// ADR 0066 clause 3: one admission's grant, before the conferral pipeline derives it. The grant is keyed on
/// the admitted roster party id and carries the admission's own per-admission role at the install root.
/// </summary>
internal sealed record AdmissionConferral(
    TenantId Tenant,
    Guid GrantId,
    string AdmittedPartyId,
    string AdmittedByPartyId,
    PermissionSet Permissions,
    DateTimeOffset IssuedAt,
    Guid Nonce,
    string SourceReference)
{
    /// <summary>The deterministic id every admission-derived record uses.</summary>
    internal static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    internal RoleReference Role =>
        new(RoleVocabularies.Domain, AccessGrantAuthorizationSeed.AdmissionRolePrefix + GrantId.ToString("N"));

    internal AuthorizationCapabilityDefinitionId DefinitionIdFor(string permission) =>
        new(StableId(GrantId.ToString("D") + ":" + permission));
}

/// <summary>
/// ck-10 (DES-0029): the authority an admission conferral carries into its authorize stage. A live admission
/// carries the signed roster it produced; an administrator's narrowing reissue carries the decision that
/// admitted the narrowing. Either is checked against the conferral itself, so a conferral naming an admitter
/// that did not sign this party's admission, or a decision over another act, is refused before any read.
/// </summary>
internal sealed class AdmissionConferralAuthority
{
    private static readonly AuthorizationOperation MembersManage =
        AuthorizationOperation.Parse(TeamRolePermissions.MembersManage);

    private readonly MemberRoster? roster;
    private readonly GrantId narrowed;

    private AdmissionConferralAuthority(MemberRoster? roster, AuthorizationDecision? decision, GrantId narrowed)
    {
        this.roster = roster;
        Decision = decision;
        this.narrowed = narrowed;
    }

    /// <summary>The decision the audit is attributed to, or null for a signed admission.</summary>
    internal AuthorizationDecision? Decision { get; }

    /// <summary>A live admission: the roster must record the admitted party as admitted by the admitter.</summary>
    internal static AdmissionConferralAuthority SignedAdmission(MemberRoster roster) =>
        new(roster ?? throw new ArgumentNullException(nameof(roster)), null, default);

    /// <summary>An administrator's narrowing of <paramref name="narrowed"/>, under its admitted decision.</summary>
    internal static AdmissionConferralAuthority Narrowing(AuthorizationDecision decision, GrantId narrowed) =>
        new(null, decision ?? throw new ArgumentNullException(nameof(decision)), narrowed);

    internal void Authorize(AdmissionConferral conferral)
    {
        if (Decision is { } decision)
        {
            decision.RequireAllowedReaction(MembersManage, conferral.Tenant, "members", narrowed.ToString());
            if (!string.Equals(decision.Request.Principal.Value, conferral.AdmittedByPartyId, StringComparison.Ordinal))
                throw new ArgumentException("The narrowing is attributed to a principal the decision did not admit.");
            return;
        }

        var admission = roster!.Find(conferral.AdmittedPartyId)?.Admission;
        if (admission is null
            || !string.Equals(admission.AdmittedByPartyId, conferral.AdmittedByPartyId, StringComparison.Ordinal))
            throw new ArgumentException("The signed roster records no admission of this party by this admitter.");
    }
}

/// <summary>What the conferral's validate stage sealed, for its commit stage to persist unchanged.</summary>
internal sealed record ValidatedAdmissionConferral(
    AccessGrant Grant,
    string SourceReference,
    IReadOnlyList<ValidatedAuthorizationConfigurationWrite> Definitions,
    IRoleVocabularyReader Roles,
    AuthorizationDecision? Decision);

/// <summary>
/// The caller's open transaction as the conferral's bind and commit stages see it: bind reads through it, and
/// commit stages every sealed record and its audit into it and saves them as one unit.
/// </summary>
internal interface IAdmissionConferralUnit
{
    ValueTask<bool> GrantExistsAsync(GrantId grant, CancellationToken ct);

    ValueTask<long> DefinitionRevisionAsync(AuthorizationCapabilityDefinitionId definition, CancellationToken ct);

    ValueTask CommitAsync(ValidatedAdmissionConferral conferral, CancellationToken ct);
}
