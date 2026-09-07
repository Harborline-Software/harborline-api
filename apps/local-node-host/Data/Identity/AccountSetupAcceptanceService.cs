using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed outcome of one account-setup invitation acceptance.</summary>
public enum AccountSetupAcceptStatus
{
    /// <summary>
    /// Full acceptance: the joiner account, canonical Party binding, initial member grant + epoch,
    /// and web tenant membership all exist. The joiner may now traverse the standard
    /// challenge → <c>/api/session/select</c> flow to establish a tenant-bound session.
    /// </summary>
    Accepted,

    /// <summary>
    /// The invitation gate refused (unknown / expired / not-yet-valid / already-consumed / revoked /
    /// wrong-purpose). Non-enumerating — the route maps this to a single generic refusal.
    /// </summary>
    InvitationRefused,

    /// <summary>
    /// The inviter no longer holds the mandate to confer the member role (lost members:manage, grant
    /// revoked/expired, or cannot attenuate to the granted role). Fail-closed (ADR 0077 §2.1-0(b)).
    /// </summary>
    AuthorityRefused,

    /// <summary>The chosen username is already owned by a different account. Fail-closed.</summary>
    UsernameConflict,

    /// <summary>The invitation was already accepted under different credential evidence. Fail-closed.</summary>
    ChangedReplay,

    /// <summary>
    /// The membership admission did not complete (transient contention / account fenced by an
    /// in-flight decision). The durable intermediate state is idempotent on the invitation identity;
    /// completing it is the #3013 recovery card's job.
    /// </summary>
    MembershipUnavailable,
}

/// <summary>Non-secret result of one acceptance.</summary>
public sealed record AccountSetupAcceptResult(
    AccountSetupAcceptStatus Status,
    string? AccountId);

/// <summary>Browser-supplied acceptance command. Only the raw code proves authority; the rest is joiner input.</summary>
public sealed record AccountSetupAcceptCommand(
    string RawCode,
    string TenantId,
    string Username,
    string CredentialHash,
    string CredentialCeremonyId);

/// <summary>
/// The public acceptance-authority boundary (mirrors <c>IWebFounderBindAuthority</c>): a public
/// interface with an internal implementation so the public route/endpoint can depend on it without
/// exposing the saga's internal collaborators.
/// </summary>
public interface IAccountSetupAcceptanceAuthority
{
    /// <summary>Accepts an account-setup invitation, provisioning the joiner's web tenant membership.</summary>
    Task<AccountSetupAcceptResult> AcceptAsync(
        AccountSetupAcceptCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The web-plane invitation-acceptance saga (MTW-2 #2614, Reading B + Option A). It turns a valid
/// unexpired invitation code into a fully-provisioned web tenant member by composing the wave's
/// version-fenced authorities in the ruled order — <b>validate before mint, grant before membership,
/// consume last</b> (admiral rulings 2026-07-22T2310Z and card 3365):
/// <list type="number">
///   <item>GATE 1 — read the valid, still-unconsumed invitation and its SIGNED inviter pins. Every
///     downstream coordinate is taken from these pins, never from the browser command.</item>
///   <item>GATE 2 — inviter mandate-attenuation re-verification: the inviter must STILL hold live
///     live authorization closure (<see cref="IAuthorizationClosureReader.UserPermissionsAsync"/> = members:manage
///     on the verified roster + active grants) AND be able to attenuate to the granted member role
///     through scoped closure coverage. This is the duty transferred to acceptance by
///     council-verdict-2026-07-22T1124Z — #2615's issuance service trusts the contract's inviter.</item>
///   <item>MINT the joiner installation account (element 1), the canonical Party → principal binding
///     (D2, element 2 web-plane), the <see cref="AdmissionCompleted"/> contract from the signed pins,
///     the initial member grant + authorization epoch (<see cref="InitialGrantIssuanceService"/>,
///     element 3, grant-BEFORE-membership), then the web tenant membership via the R3-H coordinator
///     (element 2). Each mint is idempotent on the invitation identity.</item>
///   <item>CONSUME the single-use invitation only after every mint completed. A username conflict
///     instead advances the row's durable bounded-disclosure version and leaves it retryable until
///     the third disclosure consumes it.</item>
/// </list>
/// The signed atlas roster admission is DEFERRED to the #3107 first-wire-enrollment bridge; the web
/// membership is grant-anchored (Option A, admiral-ruling-2026-07-23T0045Z). Element (4) — the first
/// tenant-bound session — is NOT minted here: acceptance leaves the substrate so the joiner's
/// standard login → challenge → <c>/api/session/select</c> flow succeeds (no mint bypass).
/// </summary>
internal sealed class AccountSetupAcceptanceService : IAccountSetupAcceptanceAuthority
{
    // Three gives a legitimate joiner two ordinary corrections plus one final choice, while bounding
    // one administrator-issued code to at most three username-availability disclosures.
    internal const int MaximumUsernameConflictDisclosures = 3;

    private readonly AccountSetupInvitationStore _invitationStore;
    private readonly IAuthorizationClosureReader _authorization;
    private readonly IWebJoinerPartyBindingMinter _partyBindingMinter;
    private readonly IInvitationAcceptanceGrantWriter _grantWriter;
    private readonly IInvitationAcceptanceMembershipWriter _membershipWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public AccountSetupAcceptanceService(
        AccountSetupInvitationStore invitationStore,
        IAuthorizationClosureReader authorization,
        IWebJoinerPartyBindingMinter partyBindingMinter,
        IInvitationAcceptanceGrantWriter grantWriter,
        IInvitationAcceptanceMembershipWriter membershipWriter,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
    {
        _invitationStore = invitationStore ?? throw new ArgumentNullException(nameof(invitationStore));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _partyBindingMinter = partyBindingMinter ?? throw new ArgumentNullException(nameof(partyBindingMinter));
        _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));
        _membershipWriter = membershipWriter ?? throw new ArgumentNullException(nameof(membershipWriter));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AccountSetupAcceptResult> AcceptAsync(
        AccountSetupAcceptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.RawCode) ||
            string.IsNullOrWhiteSpace(command.Username) ||
            string.IsNullOrWhiteSpace(command.CredentialHash) ||
            string.IsNullOrWhiteSpace(command.CredentialCeremonyId) ||
            !Guid.TryParse(command.TenantId, out var parsedTenant))
        {
            return Refused(AccountSetupAcceptStatus.InvitationRefused);
        }

        var tenantId = parsedTenant.ToString("D");
        var tenant = new TenantId(tenantId);
        var now = _timeProvider.GetUtcNow();

        // ── GATE 1: validate the still-unconsumed invitation and read its SIGNED inviter pins. ──
        var invitation = await _invitationStore
            .ReadPendingAsync(command.RawCode, tenantId, WebSetupInvitationPurpose.AccountSetup, now, cancellationToken)
            .ConfigureAwait(false);
        if (invitation is null)
        {
            return Refused(AccountSetupAcceptStatus.InvitationRefused);
        }

        // ── GATE 2: inviter mandate-attenuation re-verification (ADR 0077 §2.1-0(b)). ──
        // Resolve the inviter's live PBAC bundle. Legacy-only providers are mapped through the compatibility
        // catalog so existing hosts remain source-compatible during the migration.
        var inviterPrincipal = new PrincipalUserId(invitation.InviterPrincipalId);
        var inviterAuthorityPrincipal = new ActorId(invitation.InviterPrincipalId);
        var memberRole = AccessGrantAuthorizationSeed.MemberRole;
        var requestedPermissions = await _authorization.RolePermissionsAsync(tenant, memberRole, cancellationToken)
            .ConfigureAwait(false);
        var inviterPermissions = await _authorization.UserPermissionsAsync(
                tenant, inviterAuthorityPrincipal, now, cancellationToken)
            .ConfigureAwait(false);
        if (!inviterPermissions.Covers(requestedPermissions))
        {
            return Refused(AccountSetupAcceptStatus.AuthorityRefused);
        }

        // ── MINT 1: the joiner installation account (non-singleton minter). ──
        AccountSetupAcceptResult? mintFailure;
        string accountId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var minter = scope.ServiceProvider.GetRequiredService<WebJoinerAccountMinter>();
            var mint = await minter.MintAsync(
                    new WebJoinerAccountMintCommand(
                        invitation.InvitationId,
                        tenantId,
                        command.Username,
                        command.CredentialHash,
                        command.CredentialCeremonyId),
                    cancellationToken)
                .ConfigureAwait(false);
            (accountId, mintFailure) = mint.Status switch
            {
                WebJoinerAccountMintStatus.Created or WebJoinerAccountMintStatus.IdempotentReplay =>
                    (mint.AccountId!, (AccountSetupAcceptResult?)null),
                _ => (string.Empty, Refused(AccountSetupAcceptStatus.ChangedReplay)),
            };

            if (mint.Status == WebJoinerAccountMintStatus.UsernameConflict)
            {
                var recorded = await _invitationStore.RecordUsernameConflictAsync(
                        command.RawCode,
                        tenantId,
                        WebSetupInvitationPurpose.AccountSetup,
                        invitation.OwnerVersion,
                        MaximumUsernameConflictDisclosures,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                mintFailure = recorded
                    ? Refused(AccountSetupAcceptStatus.UsernameConflict)
                    : Refused(AccountSetupAcceptStatus.InvitationRefused);
            }
        }
        if (mintFailure is not null)
        {
            return mintFailure;
        }

        // ── The canonical tenant principal is CREATED by acceptance (never derived from the Party),
        //    deterministic on the invitation identity so every step is idempotent (ADR 0102, D2). ──
        var bootstrapAuthorization = new InvitationBootstrapAuthorization(
            accountId,
            tenant,
            now,
            invitation.InvitationId);
        var acceptanceAuthority = bootstrapAuthorization.Authority;
        var canonicalPrincipalValue = acceptanceAuthority.Principal.Value;
        var canonicalPrincipal = new PrincipalUserId(canonicalPrincipalValue);
        // The anonymous endpoint authenticates invitation possession, not a pre-existing acceptor session.
        // Binding ruling E attributes both bootstrap acts to the account/principal minted first; the inviter
        // remains provenance only and is never substituted as the actor.
        // ── MINT 2: the canonical Party → principal binding (D2). ──
        var partyReference = await _partyBindingMinter
            .MintAsync(
                tenant,
                canonicalPrincipal,
                new PartyId(canonicalPrincipalValue),
                command.Username,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // ── Build the admission contract from the SIGNED pins — never from browser input. ──
        var admission = new AdmissionCompleted(
            tenant,
            canonicalPrincipal,
            partyReference,
            inviterPrincipal,
            invitation.InvitationId,
            memberRole,
            new GrantProvenance(
                GrantSourceKind.Invitation,
                new GrantReason(GrantReasonCodes.Invitation, invitation.InvitationId),
                inviterAuthorityPrincipal));

        // ── MINT 3: the initial member grant + authorization epoch (grant BEFORE membership). ──
        var issuance = await _grantWriter.WriteAsync(
                admission,
                accountId,
                acceptanceAuthority,
                bootstrapAuthorization.AuthorizeInitialGrantIssuance(),
                cancellationToken)
            .ConfigureAwait(false);
        var grant = issuance.Grant;

        // ── MINT 4: the web tenant membership via the R3-H coordinator. The joiner self-admits over
        //    the invitation (account == actor); pin the epoch returned by the grant store so a replay or
        //    upgraded database never assumes that freshness starts at one. ──
        var correlationId = InstallationAuditIntegrity.Hash(
            "web-membership-admission/v1", invitation.InvitationId);
        var authorityEvidenceDigest = InstallationAuditIntegrity.Hash(
            "web-admission-evidence/v1", invitation.InvitationId, invitation.CommandFingerprint);
        var mutation = new TenantMembershipMutation(
            TenantId: tenantId,
            CanonicalPrincipalId: canonicalPrincipalValue,
            GrantId: grant.GrantId.ToString(),
            ExpectedGrantOwnerVersion: 1,
            AuthorizationEpoch: issuance.AuthorizationEpoch,
            ExpectedMembershipOwnerVersion: 0,
            TargetStatus: TenantMembershipStatus.Active);
        var coordination = await _membershipWriter.WriteAsync(
                new InstallationIdentityCoordinationCommand(
                    CorrelationId: correlationId,
                    AccountId: accountId,
                    ActorAccountId: accountId,
                    AuthorityEvidenceDigest: authorityEvidenceDigest,
                    ExpectedAccountOwnerVersion: 1,
                    ExpectedAccountSecurityVersion: 1,
                    ExpectedActorOwnerVersion: 1,
                    ExpectedActorSecurityVersion: 1,
                    Mutations: new[] { mutation }),
                acceptanceAuthority,
                bootstrapAuthorization.AuthorizeMembershipAdmission(),
                cancellationToken)
            .ConfigureAwait(false);
        if (coordination.Status is not (InstallationIdentityCoordinationStatus.Completed
            or InstallationIdentityCoordinationStatus.IdempotentReplay))
        {
            return new AccountSetupAcceptResult(AccountSetupAcceptStatus.MembershipUnavailable, accountId);
        }

        // ── LAST IRREVERSIBLE ACT: burn the single-use token only after every mint completed. ──
        var consumed = await _invitationStore
            .ConsumeAndReadAsync(
                command.RawCode,
                tenantId,
                WebSetupInvitationPurpose.AccountSetup,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (consumed is null)
        {
            return Refused(AccountSetupAcceptStatus.InvitationRefused);
        }

        return new AccountSetupAcceptResult(AccountSetupAcceptStatus.Accepted, accountId);
    }

    private static AccountSetupAcceptResult Refused(AccountSetupAcceptStatus status) => new(status, null);

}
