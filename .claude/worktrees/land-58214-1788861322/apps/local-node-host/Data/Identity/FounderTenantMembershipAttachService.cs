using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed disposition of one founder tenant-membership attach.</summary>
public enum FounderTenantMembershipAttachStatus
{
    /// <summary>No founder account exists yet — the bootstrap ceremony has not established one.</summary>
    SkippedNoFounder,

    /// <summary>The founder already holds a usable membership; nothing was written.</summary>
    AlreadyAttached,

    /// <summary>This run attached the founder's Party binding, tenant grant, and web tenant membership.</summary>
    Attached,

    /// <summary>
    /// A subordinate plane refused (contention, an unexpected version, a store fenced by an in-flight
    /// decision). Nothing further was written; the next start retries idempotently.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The active team is not the genesis team, so the roster identity this attach binds is not
    /// admitted in the tenant the node is serving. Nothing was written. A configuration fault.
    /// </summary>
    TenantDiverged,
}

/// <summary>
/// Attaches the bootstrap founder to the installation's already-existing first tenant (earlier repository ticket #3448).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> <see cref="InstallationFounderBootstrapService"/> mints the
/// installation identity, the account, the root key epoch and the root <i>installation</i> grant — and
/// nothing else. Tenant selection is membership-lensed, and
/// <see cref="InstallationTenantCandidateLocator"/> derives its candidate set solely from COMPLETED
/// coordinator receipts naming a tenant. The founder has none, so a freshly bootstrapped installation's
/// founder authenticates (<c>/api/session/account-challenge</c> → 200) and then cannot select a tenant
/// (<c>/api/session/select</c> → 401) — verified on a live host on 2026-07-30, with both a null tenant
/// and an explicit one. The one account a fresh install creates is the one account that cannot complete
/// the v2 flow, while an invited joiner can, because
/// <see cref="AccountSetupAcceptanceService"/> writes the membership this path omits.
/// </para>
/// <para>
/// <b>Why this is licensed.</b> ADR 0160 <b>R3-E</b>: <i>"Installation founder-account bootstrap may run
/// exactly once when the installation has no account. It may attach the already-existing first tenant's
/// founder membership in the same audited ceremony."</i> D2's prohibitions name different artefacts and
/// are not touched here: nothing is written to the signed <c>MemberRoster</c> / atlas plane (no
/// trust-roster admission is created or expanded), and the grant minted here is a TENANT grant, not the
/// installation root grant.
/// </para>
/// <para>
/// <b>Why it is not inside the bootstrap transaction.</b> That is a single serializable transaction over
/// the installation-identity store. This work is cross-store — tenant authority document, installation
/// coordinator, grant store, and the People Party — and the R3-H coordinator is the mechanism already
/// designed for exactly that. R3-E says "ceremony", not "transaction". It also must run AFTER
/// <c>MultiTeamBootstrapHostedService</c> has materialized the genesis team.
/// </para>
/// <para>
/// <b>Failure posture.</b> A refusal logs a classified reason and returns; it does NOT abort host start.
/// A missing tenant membership is an availability problem in a subordinate plane, and bricking the host
/// over it would be the "availability outcome wearing a security justification" that the bootstrap
/// ceremony's own remarks already warn against. The founder-authority failure that DOES abort start is a
/// different and stronger condition.
/// </para>
/// </remarks>
internal sealed class FounderTenantMembershipAttachService
{
    /// <summary>Domain separator for the founder's canonical tenant principal.</summary>
    private const string PrincipalLabel = "web-tenant-principal/v1";

    /// <summary>Domain separator for this attach's coordinator correlation.</summary>
    private const string MembershipLabel = "web-membership-admission/v1";

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly InstallationIdentityCoordinatorService _coordinator;
    private readonly InstallationFounderBootstrapCeremony _founderCeremony;
    private readonly BootstrapClaimRedemptionService _claimRedemption;
    private readonly ICanonicalPrincipalPartyReader _partyReader;
    private readonly IPartyWriteService _partyWriter;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly GenesisTeamIdProvider _genesisTeam;
    private readonly FounderRosterPartyProvider _rosterParty;
    private readonly TimeProvider _time;

    public FounderTenantMembershipAttachService(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        InstallationIdentityCoordinatorService coordinator,
        InstallationFounderBootstrapCeremony founderCeremony,
        BootstrapClaimRedemptionService claimRedemption,
        ICanonicalPrincipalPartyReader partyReader,
        IPartyWriteService partyWriter,
        IActiveTeamAccessor activeTeam,
        GenesisTeamIdProvider genesisTeam,
        FounderRosterPartyProvider rosterParty,
        TimeProvider time)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _founderCeremony = founderCeremony ?? throw new ArgumentNullException(nameof(founderCeremony));
        _claimRedemption = claimRedemption ?? throw new ArgumentNullException(nameof(claimRedemption));
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
        _partyWriter = partyWriter ?? throw new ArgumentNullException(nameof(partyWriter));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _genesisTeam = genesisTeam ?? throw new ArgumentNullException(nameof(genesisTeam));
        _rosterParty = rosterParty ?? throw new ArgumentNullException(nameof(rosterParty));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The founder's canonical tenant principal. Deterministic in the ceremony coordinates.</summary>
    /// <remarks>
    /// Keyed on the ceremony correlation and the tenant — NOT on the account id, which a governed
    /// recovery may legitimately change, and never on <c>ActiveTeamAuthorizationContext.LocalUserId</c>, which
    /// ADR 0160 R3-D forbids the web plane from falling back to.
    /// </remarks>
    internal static PrincipalUserId DerivePrincipal(TenantId tenant, string ceremonyCorrelationId) =>
        new(InstallationAuditIntegrity.Hash(PrincipalLabel, tenant.ToString(), ceremonyCorrelationId));

    public async Task<FounderTenantMembershipAttachStatus> RunAsync(CancellationToken cancellationToken)
    {
        var admittedAt = _time.GetUtcNow();
        // The GENESIS tenant -- the one the signed roster, the /admission/invites trust anchor, and
        // therefore AccountSetupInvitationIssuer all speak. NOT the active team.
        //
        // An earlier revision of this file used NodeTenant.Resolve(_activeTeam), which is wrong on any
        // multi-team host. Program.cs's comment asserts "daemon-active-team == roster-genesis-team",
        // but MultiTeamBootstrapHostedService only honours GenesisTeamIdProvider in its LEGACY
        // single-team branch; the multi-team branch activates each configured TeamBootstraps[].TeamId
        // and never consults the provider. The committed dogfood production config takes that branch
        // with LocalNode:TeamId unset, so the two genuinely diverge there. Binding the roster's party
        // id into a tenant whose roster does not admit it makes `select` return 200 while the founder
        // still cannot invite anyone -- the exact silent half-failure this card exists to avoid.
        var genesisTeam = _genesisTeam.TeamId;
        var tenant = ActiveTeamTenantContext.ProjectTenantId(genesisTeam);

        // Fail closed, loudly, rather than bind into the wrong tenant. On a host where the active team
        // is not the genesis team the roster identity this attach depends on is not admitted there, and
        // the genesis team may not even be materialized. That is a configuration fault to surface, not
        // a condition to paper over.
        var active = _activeTeam.Active;
        if (active is not null && active.TeamId != genesisTeam)
        {
            return FounderTenantMembershipAttachStatus.TenantDiverged;
        }
        var ceremonyCorrelation = InstallationFounderBootstrapCeremony.CorrelationId;

        // ── Step 0: resolve the founder account BY KEY, never by cardinality. ──────────────────────
        // The bootstrap service resolves the founder the same way on replay. Taking "the only row in
        // Accounts" is a live brick: WebJoinerAccountMinter makes that table multi-row the moment one
        // invitation is redeemed.
        await using var context = await _identityFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rootGrant = await context.InstallationAccessGrants.AsNoTracking()
            .SingleOrDefaultAsync(g => g.AuditCorrelationId == ceremonyCorrelation, cancellationToken)
            .ConfigureAwait(false);
        if (rootGrant is null)
        {
            return FounderTenantMembershipAttachStatus.SkippedNoFounder;
        }

        var account = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.AccountId == rootGrant.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return FounderTenantMembershipAttachStatus.SkippedNoFounder;
        }

        var installation = await context.InstallationIdentities.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null)
        {
            return FounderTenantMembershipAttachStatus.SkippedNoFounder;
        }

        var principal = DerivePrincipal(tenant, ceremonyCorrelation);

        // ── Step 2: bind the Party to the EXISTING roster genesis party id. ────────────────────────
        // This is the half that is easy to miss, and it is independently load-bearing: bind to a fresh
        // id instead and `select` still returns 200 while the founder can never invite anyone, because
        // AccountSetupInvitationIssuer and WebRosterGranterAuthorityProvider both resolve the party id
        // IN THE ROSTER. It writes nothing to the roster — it adopts the id the roster already holds.
        var binding = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null)
        {
            var rosterPartyId = new PartyId(_rosterParty.PartyId);

            // ADOPT-OR-CREATE, not create. The id here is FIXED (the roster's), so unlike
            // WebJoinerPartyBindingMinter -- which mints a fresh id and can safely retry -- a crash
            // between this insert and the role edge below would leave an orphan `parties` row and every
            // later start would die on the primary key, be caught by the hosted runner, and log forever.
            // IPartyReadModel cannot serve the existence check: it is Guid-keyed and a roster party id
            // is `os:<user>#<hex8>`. So the insert itself is the probe.
            try
            {
                await _partyWriter.CreateAsync(
                        tenant,
                        PartyKind.Person,
                        "Founder",
                        rosterPartyId,
                        admittedAt,
                        id: rosterPartyId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // The row already exists -- an orphan from an interrupted earlier attach. Adopt it and
                // complete the binding; AttachRoleAsync is idempotent on (PartyId, RoleName, RoleRecordId).
            }

            await _partyWriter.AttachRoleAsync(
                    rosterPartyId,
                    NodeEfPartyRepository.PrincipalUserBindingRoleName,
                    principal.Value,
                    rosterPartyId,
                    admittedAt,
                    cancellationToken)
                .ConfigureAwait(false);

            binding = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
                .ConfigureAwait(false);
            if (binding is null)
            {
                return FounderTenantMembershipAttachStatus.Unavailable;
            }
        }

        // ── Step 3: issue the founder's TENANT grant + authorization epoch (grant BEFORE membership). ──
        // The install layer grants the founder Administrator-grade tenant authority. The root grant's
        // issuer is the granter, so this imports authority instead of asserting it through the founder
        // principal itself. The source-reference replay remains a restart-idempotent no-op.
        var target = new BootstrapGrantTarget(
            tenant,
            principal,
            binding.PartyId,
            ceremonyCorrelation,
            installation.InstallationIdentityId);
        var claim = await _founderCeremony.IssueClaimForTargetAsync(target, cancellationToken)
            .ConfigureAwait(false);
        BootstrapClaimRedemptionResult? issuance = claim is null
            ? null
            : await _claimRedemption.RedeemAsync(claim, target, cancellationToken).ConfigureAwait(false);
        if (issuance?.Status != BootstrapClaimRedemptionStatus.Redeemed)
        {
            issuance = await _claimRedemption.ResolveRedeemedAsync(target, cancellationToken)
                .ConfigureAwait(false);
        }
        if (issuance?.Grant is null || issuance.AuthorizationEpoch is null)
        {
            return FounderTenantMembershipAttachStatus.Unavailable;
        }
        var grant = issuance.Grant;

        // ── Step 4: attach the membership through the R3-H coordinator. ───────────────────────────
        // This single call writes BOTH missing rows: the TenantMembershipDocument (which is what carries
        // the account→principal binding) and the completed coordinator receipt (the discovery index the
        // candidate locator reads). A deterministic correlation makes a restart an IdempotentReplay.
        var correlationId = InstallationAuditIntegrity.Hash(MembershipLabel, ceremonyCorrelation);
        var evidenceDigest = InstallationAuditIntegrity.Hash(
            "web-admission-evidence/v1", ceremonyCorrelation, rootGrant.GrantId);

        var mutation = new TenantMembershipMutation(
            TenantId: tenant,
            CanonicalPrincipalId: principal.Value,
            GrantId: grant.GrantId.ToString(),
            ExpectedGrantOwnerVersion: 1,
            AuthorizationEpoch: issuance.AuthorizationEpoch.Value,
            ExpectedMembershipOwnerVersion: 0,
            TargetStatus: TenantMembershipStatus.Active);

        var coordination = await _coordinator.ExecuteAsync(
                new InstallationIdentityCoordinationCommand(
                    CorrelationId: correlationId,
                    AccountId: account.AccountId,
                    ActorAccountId: account.AccountId,
                    AuthorityEvidenceDigest: evidenceDigest,
                    // Read from the account rather than hardcoded to 1. The joiner may hardcode because
                    // it just minted the account; the founder's versions may have moved through a
                    // governed recovery before this first runs on an upgraded installation.
                    ExpectedAccountOwnerVersion: account.OwnerVersion,
                    ExpectedAccountSecurityVersion: account.SecurityVersion,
                    ExpectedActorOwnerVersion: account.OwnerVersion,
                    ExpectedActorSecurityVersion: account.SecurityVersion,
                    Mutations: new[] { mutation }),
                InstallationIdentityCoordinatorContinuation.FounderAttachment,
                cancellationToken)
            .ConfigureAwait(false);

        return coordination.Status switch
        {
            InstallationIdentityCoordinationStatus.Completed =>
                FounderTenantMembershipAttachStatus.Attached,
            InstallationIdentityCoordinationStatus.IdempotentReplay =>
                FounderTenantMembershipAttachStatus.AlreadyAttached,
            _ => FounderTenantMembershipAttachStatus.Unavailable,
        };
    }
}

/// <summary>The genesis roster party id, captured at composition so this plane can bind to it.</summary>
internal sealed class FounderRosterPartyProvider
{
    public FounderRosterPartyProvider(string partyId)
    {
        if (string.IsNullOrWhiteSpace(partyId))
        {
            throw new ArgumentException("A genesis roster party id is required.", nameof(partyId));
        }

        PartyId = partyId;
    }

    public string PartyId { get; }
}

/// <summary>
/// Runs the founder attach once at start, AFTER the genesis team is materialized. Never aborts host
/// start — see the failure-posture note on <see cref="FounderTenantMembershipAttachService"/>.
/// </summary>
internal sealed class FounderTenantMembershipAttachHostedService : IHostedService
{
    private readonly FounderTenantMembershipAttachService _attach;
    private readonly ILogger<FounderTenantMembershipAttachHostedService> _logger;

    public FounderTenantMembershipAttachHostedService(
        FounderTenantMembershipAttachService attach,
        ILogger<FounderTenantMembershipAttachHostedService> logger)
    {
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        FounderTenantMembershipAttachStatus status;
        try
        {
            status = await _attach.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Availability, not authority: log and continue. The next start retries idempotently.
            _logger.LogError(
                ex,
                "founder-membership.attach_failed: the founder tenant membership could not be attached; " +
                "the founder will be unable to select a tenant until this succeeds.");
            return;
        }

        switch (status)
        {
            case FounderTenantMembershipAttachStatus.Attached:
                _logger.LogInformation(
                    "founder-membership.attached: the founder's Party binding, tenant grant, and web " +
                    "tenant membership are established.");
                break;
            case FounderTenantMembershipAttachStatus.AlreadyAttached:
                _logger.LogInformation(
                    "founder-membership.already_attached: the founder already holds a usable membership.");
                break;
            case FounderTenantMembershipAttachStatus.SkippedNoFounder:
                _logger.LogInformation(
                    "founder-membership.skipped_no_founder: no founder account exists yet.");
                break;
            case FounderTenantMembershipAttachStatus.TenantDiverged:
                _logger.LogError(
                    "founder-membership.tenant_diverged: the active team is not the genesis team, so the " +
                    "roster identity this attach binds is not admitted in the tenant being served. Nothing " +
                    "was written. The founder cannot select a tenant or issue invitations until the host's " +
                    "team configuration is reconciled (LocalNode:TeamId vs LocalNode:MultiTeam:TeamBootstraps).");
                break;
            default:
                _logger.LogWarning(
                    "founder-membership.unavailable: a subordinate plane refused the attach; the founder " +
                    "cannot select a tenant until a later start succeeds.");
                break;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class FounderTenantMembershipAttachComposition
{
    public static IServiceCollection AddFounderTenantMembershipAttach(
        this IServiceCollection services,
        string genesisRosterPartyId)
    {
        services.AddSingleton(new FounderRosterPartyProvider(genesisRosterPartyId));
        services.AddSingleton<FounderTenantMembershipAttachService>();
        services.AddHostedService<FounderTenantMembershipAttachHostedService>();
        return services;
    }
}
