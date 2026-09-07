using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

public sealed record AdmissionCompleted
{
    public AdmissionCompleted(TenantId tenantId, PrincipalUserId canonicalTenantPrincipal,
        CanonicalPartyReference admittedPartyId, PrincipalUserId inviterPrincipal,
        string invitationIdentity, RoleReference role, GrantProvenance grant)
    {
        if (string.IsNullOrWhiteSpace(invitationIdentity) || invitationIdentity.Length > 128)
            throw new ArgumentException("A bounded invitation identity is required.", nameof(invitationIdentity));
        TenantId = tenantId; CanonicalTenantPrincipal = canonicalTenantPrincipal; AdmittedPartyId = admittedPartyId;
        InviterPrincipal = inviterPrincipal; InvitationIdentity = invitationIdentity; Role = role; Grant = grant;
    }
    public TenantId TenantId { get; }
    public PrincipalUserId CanonicalTenantPrincipal { get; }
    public CanonicalPartyReference AdmittedPartyId { get; }
    public PrincipalUserId InviterPrincipal { get; }
    public string InvitationIdentity { get; }
    public RoleReference Role { get; }
    public GrantProvenance Grant { get; }
}

public sealed class InitialGrantIssuanceService(
    IGrantStore grantStore,
    AuthorizationGate gate,
    TimeProvider timeProvider) : IInvitationAcceptanceGrantWriter
{
    private readonly AuthorizationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal DateTimeOffset CurrentInstant => _timeProvider.GetUtcNow();

    public async Task<AccessGrant> IssueAsync(
        AdmissionCompleted admission,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
        => (await IssueWithEpochAsync(admission, authority, cancellationToken).ConfigureAwait(false)).Grant;

    public async Task<InitialGrantIssuanceResult> IssueWithEpochAsync(
        AdmissionCompleted admission,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
        => await IssueWithEpochAsync(admission, authority, GranterKind.Person, null, null, cancellationToken).ConfigureAwait(false);

    internal async Task<InitialGrantIssuanceResult> IssueWithEpochAsync(
        AdmissionCompleted admission,
        string mintedAccountId,
        AuthorizationWriteContext authority,
        AuthorizationDecision invitationBootstrapDecision,
        CancellationToken cancellationToken = default)
        => await IssueWithEpochAsync(
            admission, authority, GranterKind.Person, invitationBootstrapDecision, mintedAccountId, cancellationToken).ConfigureAwait(false);

    Task<InitialGrantIssuanceResult> IInvitationAcceptanceGrantWriter.WriteAsync(
        AdmissionCompleted admission,
        string mintedAccountId,
        AuthorizationWriteContext authority,
        AuthorizationDecision decision,
        CancellationToken cancellationToken) =>
        IssueWithEpochAsync(admission, mintedAccountId, authority, decision, cancellationToken);

    internal PreparedInitialGrant PrepareBootstrapGrant(AdmissionCompleted admission, DateTimeOffset at)
    {
        ValidateBootstrap(admission);
        return PrepareGrant(admission, GranterKind.Installer, at);
    }

    private async Task<InitialGrantIssuanceResult> IssueWithEpochAsync(
        AdmissionCompleted admission,
        AuthorizationWriteContext authority,
        GranterKind granterKind,
        AuthorizationDecision? invitationBootstrapDecision,
        string? mintedAccountId,
        CancellationToken cancellationToken)
    {
        if (admission.TenantId != authority.Tenant)
            throw new ArgumentException("The grant admission does not match the write authority.", nameof(admission));
        var invitationOnboarding =
            admission.Grant.Source == GrantSourceKind.Invitation
            && string.Equals(
                admission.CanonicalTenantPrincipal.Value,
                authority.Principal.Value,
                StringComparison.Ordinal);
        if (!invitationOnboarding
            && !string.Equals(admission.InviterPrincipal.Value, authority.Principal.Value, StringComparison.Ordinal))
            throw new ArgumentException("The grant admission does not match the write authority.", nameof(admission));
        var targetGrantId = GrantIdFor(admission);
        if (invitationOnboarding)
        {
            InvitationBootstrapDecisionValidation.RequireInitialGrantIssuance(
                invitationBootstrapDecision ?? throw new ArgumentException(
                    "Invitation onboarding requires its explicit bootstrap decision.",
                    nameof(invitationBootstrapDecision)),
                mintedAccountId ?? throw new ArgumentException(
                    "Invitation onboarding requires its minted account identity.",
                    nameof(mintedAccountId)),
                authority);
        }
        else
        {
            var decision = await _gate.DecideAsync(
                authority.Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                    "members",
                    targetGrantId.Value.ToString()),
                cancellationToken).ConfigureAwait(false);
            decision.RequireAllowed();
        }
        var prepared = PrepareGrant(admission, granterKind, authority.At, authority.Principal);
        var intended = prepared.Grant;
        var persisted = await grantStore.AppendAsync(admission.TenantId, intended,
            prepared.SourceReference, cancellationToken).ConfigureAwait(false);
        if (persisted != intended) throw new InvalidOperationException("identity.initial_grant_idempotency_conflict");
        if (grantStore is not IGrantAuthorizationEpochReader epochs)
            throw new InvalidOperationException("identity.initial_grant_epoch_reader_unavailable");
        var epoch = await epochs.ReadAuthorizationEpochAsync(
            admission.TenantId, new ActorId(admission.CanonicalTenantPrincipal.Value), cancellationToken)
            .ConfigureAwait(false);
        if (epoch is null or <= 0)
            throw new InvalidOperationException("identity.initial_grant_epoch_unavailable");
        return new InitialGrantIssuanceResult(persisted, epoch.Value);
    }

    private PreparedInitialGrant PrepareGrant(
        AdmissionCompleted admission,
        GranterKind granterKind,
        DateTimeOffset at,
        ActorId? actingPrincipal = null)
    {
        var issuedAt = at;
        var grant = new AccessGrant(GrantIdFor(admission), admission.TenantId,
            new ActorId(admission.CanonicalTenantPrincipal.Value), admission.Role, ScopeExpression.Parse("/"),
            GrantResidency.Cache, new GrantValidity(issuedAt), granterKind,
            actingPrincipal ?? new ActorId(admission.InviterPrincipal.Value), issuedAt, admission.Grant, issuedAt);
        return new PreparedInitialGrant(grant, SourceReferenceFor(admission));
    }

    private static void ValidateBootstrap(AdmissionCompleted admission)
    {
        if (admission.Grant.Source != GrantSourceKind.Bootstrap ||
            admission.Grant.Reason.Code != GrantReasonCodes.Bootstrap ||
            admission.Grant.Approver.Value != admission.InviterPrincipal.Value)
            throw new InvalidOperationException("identity.bootstrap_grant_provenance_invalid");
    }

    internal static string SourceReferenceFor(AdmissionCompleted admission)
    {
        var digest = InstallationAuditIntegrity.Hash("initial-member-grant-source/v1",
            admission.TenantId.Value, admission.InvitationIdentity);
        return $"identity-admission/v1:{digest[..32]}";
    }

    internal static GrantId GrantIdFor(AdmissionCompleted admission)
    {
        var digest = InstallationAuditIntegrity.Hash("initial-member-grant/v1", admission.TenantId.Value,
            admission.CanonicalTenantPrincipal.Value, admission.AdmittedPartyId.Value,
            admission.InviterPrincipal.Value, admission.InvitationIdentity);
        return new GrantId(Guid.ParseExact(digest[..32], "N"));
    }
}

public sealed record InitialGrantIssuanceResult(AccessGrant Grant, long AuthorizationEpoch);

internal sealed record PreparedInitialGrant(AccessGrant Grant, string SourceReference);
