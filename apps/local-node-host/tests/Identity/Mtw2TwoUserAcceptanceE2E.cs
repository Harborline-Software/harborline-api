using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Versions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using BankingServices = (
    Harborline.Api.Blocks.Banking.Services.IBankAccountRepository AccountRepo,
    Harborline.Api.LocalNodeHost.Data.Financial.NodeBankAccountWriter AccountWriter,
    Harborline.Api.Blocks.Banking.Services.IStatementLineRepository LineRepo,
    Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository LinkRepo,
    Harborline.Api.Blocks.Banking.Services.IReconciliationRepository ReconciliationRepo,
    Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository PeriodRepo,
    Harborline.Api.Blocks.Banking.Import.ImportPipelineService ImportPipeline,
    Harborline.Api.Blocks.Banking.Matching.AcceptMatchService AcceptMatchService,
    Harborline.Api.Blocks.Banking.Matching.UnMatchService UnMatchService,
    Harborline.Api.Blocks.Banking.Matching.ReconciliationLockLease ReconciliationLease,
    Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider FeedProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.Banking.NodeLocalBankFeedDbContext> FeedConnectionFactory);
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Harborline.Api.LocalNodeHost.Tests.TestDoubles;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #2620 — the wave convergence gate. ONE production-shaped two-user acceptance E2E that drives
/// the REAL web-plane identity substrate end to end: founder bootstrap (which designates the founder
/// itself since earlier repository ticket #3373, so founder-bind answers AlreadyDesignated) → founder session (real
/// challenge → select → materialize principal) → invite →
/// joiner accept (REAL saga: real grant + epoch + membership) → joiner session → BOTH principals perform
/// a REAL journal-entry write carrying their OWN distinct node-signed attribution envelope → founder
/// revokes the joiner via <see cref="AdminTeamAccessAuthority.RevokeMemberGrantAsync"/> → the joiner's
/// next <see cref="WebSelectedSessionPrincipalAuthority.AuthenticateAsync"/> returns null (the fence
/// refuses on the revoked grant).
/// </summary>
/// <remarks>
/// <para><b>The three mutation-proof teeth (all REAL; reverting any one breaks a named assert):</b></para>
/// <list type="number">
///   <item><b>Fence</b> = the REAL <see cref="LiveTenantMembershipAuthorityAdmission"/>, reached via
///     <c>WebSelectedSessionPrincipalAuthority.AuthenticateAsync →
///     InstallationIdentityCoordinatorService.ResolveUsableMembershipAsync → admission</c>. Revert it to an
///     always-admit stub and step 9 returns non-null → the <c>Assert.Null(joinerAfterRevoke)</c> FAILS.</item>
///   <item><b>Revocation</b> = the REAL <see cref="AdminTeamAccessAuthority.RevokeMemberGrantAsync"/> over
///     the REAL <see cref="NodeEfGrantStore"/> (sets <c>grant.RevokedAtUnixMs</c>). Skip/neuter it and the
///     grant stays live → step 9 admits → the same assert FAILS.</item>
///   <item><b>Signature-per-author</b> = the REAL <see cref="NodeEfJournalStore"/> + attribution envelope
///     materialized from the REAL <see cref="SelectedSessionRequestPrincipal"/> published into the
///     listener's attribution scope. Collapse the attribution to the operator (unbind the request) and both
///     writes carry the SAME operator party → the distinct-<c>member_party_id</c> assert FAILS.</item>
/// </list>
/// <para><b>Attribution wording (board finding F9).</b> This asserts ATTESTATION INTEGRITY + TAMPER
/// EVIDENCE only — NEVER impersonation-resistance. Both writes are signed by the SAME node key; the
/// distinct actor is the distinct <c>member_party_id</c> INSIDE the signed payload.</para>
/// <para><b>Real vs substituted seams.</b> REAL: bootstrap, founder-bind, challenge issuer, tenant
/// selection authority, selected-session store, selected-session principal authority (the fence-invoking
/// materializer), R3-H coordinator, encrypted tenant-membership store, the fence, NodeEfGrantStore +
/// InitialGrantIssuanceService, AdminTeamAccessAuthority.IssueInvitationAsync (→ the REAL
/// AccountSetupInvitationIssuer members:manage + grant-pin + attenuation gate) and
/// AdminTeamAccessAuthority.RevokeMemberGrantAsync, NodeEfJournalStore + node-signed attribution envelope,
/// real Argon2id hasher. SUBSTITUTED (only the recipe-listed / not-a-tooth pieces): FixturePartitionResolver
/// (the membership store stays the REAL encrypted store); the live authorization closure +
/// IWebJoinerPartyBindingMinter fakes copied from AccountSetupAcceptanceServiceTests; a seeded-fixture
/// ICanonicalPrincipalPartyReader (recipe-permitted); a FixedRosterReader over a REAL Ed25519-signed
/// MemberRoster (mirrors the platform's own AdminTeamAccessAuthorityTests — the roster is not one of the
/// three teeth). No invitation/session/grant/membership row is inserted directly to fake state; every one
/// is created by the real substrate — the founder now ISSUES the joiner's invitation through the real
/// admin-gated authority (#3123 / council F1), closing the last real-flow seam. Clocks are frozen
/// (FixedTimeProvider) throughout and every actor traverses the real challenge → select → act routes — no
/// backdoor into session/roster STATE.</para>
/// </remarks>
[Trait("PlanCard", "MTW-2-2620")]
[Trait("PlanCard", "MTW-2-3377")]
public sealed class Mtw2TwoUserAcceptanceE2E
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 9, 25, 0, TimeSpan.Zero);

    // The web team tenant every member is bound into. A real Guid — the selection/fence/roster all
    // canonicalise it to "D" form.
    private const string TeamTenantId = "7e57aaaa-0000-0000-0000-00000000e2e0";

    // The node's local ledger tenant (the financial store the journal writes land in). Distinct from the
    // team tenant — the attribution envelope records WHO acted regardless of the ledger tenant.
    private static readonly TenantId LedgerTenantId = new("local");

    private const string FounderUsername = "founder";
    private const string FounderPassword = "correct horse battery staple founder";
    private const string FounderPrincipal = "principal-founder";
    private const string FounderParty = "party-founder";

    private const string JoinerUsername = "joiner";
    private const string JoinerPassword = "correct horse battery staple joiner";

    private static AuthorizationWriteContext FounderAuthority(string tenantId) =>
        new(new ActorId(FounderPrincipal), new TenantId(tenantId), Now);

    [Fact]
    public async Task Real_Admin_Refuses_Permission_Escalation_And_Leaves_Grant_Unchanged()
    {
        var setup = await CreateAcceptedMembersAsync(PermissionSet.Of(
            TeamRolePermissions.MembersManage,
            Permission.ContactsRead));
        await using var h = setup.Harness;

        var before = await h.ReadGrantRowAsync(setup.JoinerGrantId);
        var result = await h.AdminTeam.UpdateMemberPermissionsAsync(
            setup.FounderSelectedHandle,
            setup.TenantId,
            setup.JoinerGrantId,
            new[] { Permission.ContactsRead, Permission.ContactsCreate },
            FounderAuthority(setup.TenantId));

        // Ticket 294 slice 2a: the founder's admin session now resolves under the ONE key, so the refusal
        // is the ticket-204 retired-mutation refusal itself rather than a denied session. Either way the
        // escalation is refused and NOTHING is written -- which is what this test claims.
        Assert.NotNull(result);
        Assert.Equal(AdminUpdateMemberPermissionsStatus.NotFound, result!.Status);
        var after = await h.ReadGrantRowAsync(setup.JoinerGrantId);
        Assert.Equal(before.OwnerVersion, after.OwnerVersion);
        Assert.Equal(before.RoleName, after.RoleName);
    }

    [Fact]
    public async Task Real_Admin_Refuses_Self_Update_And_Leaves_Grant_Unchanged()
    {
        var setup = await CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var founderGrantId = await h.ResolveFounderGrantIdAsync(setup.TenantId);
        var before = await h.ReadGrantRowAsync(founderGrantId);

        var result = await h.AdminTeam.UpdateMemberPermissionsAsync(
            setup.FounderSelectedHandle,
            setup.TenantId,
            founderGrantId,
            new[] { Permission.ContactsRead },
            FounderAuthority(setup.TenantId));

        Assert.NotNull(result);
        Assert.Equal(AdminUpdateMemberPermissionsStatus.SelfUpdateRefused, result!.Status);
        var after = await h.ReadGrantRowAsync(founderGrantId);
        Assert.Equal(before.OwnerVersion, after.OwnerVersion);
        Assert.Equal(before.RoleName, after.RoleName);
    }

    [Fact]
    public async Task Real_Admin_Refuses_Editing_A_Target_With_A_Signed_Roster_Edge()
    {
        var setup = await CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        await h.AdmitInvitationTargetToRosterAsync(setup.InvitationId);
        var before = await h.ReadGrantRowAsync(setup.JoinerGrantId);

        var result = await h.AdminTeam.UpdateMemberPermissionsAsync(
            setup.FounderSelectedHandle,
            setup.TenantId,
            setup.JoinerGrantId,
            new[] { Permission.ContactsRead, Permission.ContactsCreate },
            FounderAuthority(setup.TenantId));

        Assert.NotNull(result);
        Assert.Equal(AdminUpdateMemberPermissionsStatus.NotFound, result!.Status);
        var after = await h.ReadGrantRowAsync(setup.JoinerGrantId);
        Assert.Equal(before.OwnerVersion, after.OwnerVersion);
        Assert.Equal(before.RoleName, after.RoleName);
    }

    [Fact]
    public async Task Two_User_Acceptance_Fence_Revocation_And_Per_Author_Attribution()
    {
        await using var h = await Harness.CreateAsync();
        var tenantId = Guid.Parse(TeamTenantId).ToString("D");

        // ── Step 1: founder bootstrap (real ceremony; founder account Active v1). ────────────────────
        var founderHash = h.Hasher.HashPassword(HashUser, FounderPassword);
        var bootstrap = await h.Bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
            FounderUsername,
            founderHash,
            Guid.NewGuid().ToString("N"),
            string.Join(":", Enumerable.Repeat("AB", 32)),
            "founder-bootstrap-2620"));
        Assert.Equal(InstallationFounderBootstrapStatus.Created, bootstrap.Status);
        var founderAccountId = bootstrap.AccountId!;

        // Provision the founder's REAL grant + epoch (InitialGrantIssuanceService) then REAL membership
        // (the R3-H coordinator) — no direct membership/grant row insertion; the same authorities the
        // acceptance saga uses. This is what makes the founder's challenge → select resolve a usable
        // membership through the fence.
        var founderIssuance = await h.GrantIssuance.IssueWithEpochAsync(new AdmissionCompleted(
            new TenantId(tenantId),
            new PrincipalUserId(FounderPrincipal),
            new CanonicalPartyReference(FounderParty),
            new PrincipalUserId(FounderPrincipal),
            "founder-genesis-admission-2620", RoleReference.Administrator,
            new GrantProvenance(GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap), new ActorId(FounderPrincipal))));
        var founderMembership = await h.Coordinator.ExecuteAsync(new InstallationIdentityCoordinationCommand(
            CorrelationId: Digest("founder-membership-2620"),
            AccountId: founderAccountId,
            ActorAccountId: founderAccountId,
            AuthorityEvidenceDigest: Digest("founder-evidence-2620"),
            ExpectedAccountOwnerVersion: 1,
            ExpectedAccountSecurityVersion: 1,
            ExpectedActorOwnerVersion: 1,
            ExpectedActorSecurityVersion: 1,
            Mutations: new[]
            {
                new TenantMembershipMutation(
                    TenantId: tenantId,
                    CanonicalPrincipalId: FounderPrincipal,
                    GrantId: founderIssuance.Grant.GrantId.ToString(),
                    ExpectedGrantOwnerVersion: 1,
                    AuthorizationEpoch: founderIssuance.AuthorizationEpoch,
                    ExpectedMembershipOwnerVersion: 0,
                    TargetStatus: TenantMembershipStatus.Active),
            }));
        Assert.Equal(InstallationIdentityCoordinationStatus.Completed, founderMembership.Status);

        // ── Step 2: founder-bind meets a founder the ceremony already designated. ───────────────────
        var founderBindRequest = new WebFounderBindRequest(
            founderAccountId,
            new TenantId(tenantId),
            new PrincipalUserId(FounderPrincipal),
            new CanonicalPartyReference(FounderParty),
            ExpectedSourceVersion: 1,
            IdempotencyKey: "founder-bind-idem-2620",
            AuditCorrelationId: "founder-bind-audit-2620");
        // Since earlier repository ticket #3373 the bootstrap ceremony designates the founder itself, so by the time
        // this route is reachable the installation is ALREADY designated and founder-bind correctly
        // declines. That is the point of the card: the founder is a fact from first boot rather than
        // something a later call has to establish, and there is no window in which the role line
        // reads unresolved. Membership is derived by comparing the session's AccountId against
        // designation.AccountId (WebSelectedSessionIdentityAuthority), and the ceremony sets that
        // column to the founder account it creates -- so nothing downstream needs this bind.
        var firstBind = await h.FounderBind.BindAsync(founderBindRequest);
        Assert.Equal(WebFounderBindStatus.AlreadyDesignated, firstBind.Status);
        var reBind = await h.FounderBind.BindAsync(founderBindRequest);
        Assert.Equal(WebFounderBindStatus.AlreadyDesignated, reBind.Status);

        // Keep the legacy shape covered. After earlier repository ticket #3373 `Bound` is reachable in production ONLY
        // on an install bootstrapped before that change, and this was the sole test driving real
        // ceremony -> real binding service -> Bound. Drop the designation to model such an install,
        // assert the bind still works, and put it back so the rest of the E2E runs against the
        // production-current state.
        await using (var legacy = await h.IdentityFactory.CreateDbContextAsync())
        {
            legacy.RootDesignations.RemoveRange(await legacy.RootDesignations.ToArrayAsync());
            await legacy.SaveChangesAsync();
        }

        var legacyBind = await h.FounderBind.BindAsync(founderBindRequest with
        {
            IdempotencyKey = "founder-bind-idem-2620-legacy",
        });
        Assert.Equal(WebFounderBindStatus.Bound, legacyBind.Status);

        // ── Step 3: founder session — REAL challenge → select → materialize principal. ───────────────
        var founderSelectedHandle = await LoginAndSelectAsync(h, FounderUsername, FounderPassword, tenantId);
        var founderPrincipal = await h.SelectedSessionPrincipals.AuthenticateAsync(founderSelectedHandle);
        Assert.NotNull(founderPrincipal);
        Assert.Equal(FounderParty, founderPrincipal!.CanonicalParty.Value);

        // ── Step 4: the founder ISSUES the joiner's invitation through the REAL admin-gated authority
        //    (AdminTeamAccessAuthority.IssueInvitationAsync → AccountSetupInvitationIssuer), driving the
        //    members:manage roster gate + grant-pin revalidation + requested-permission attenuation on
        //    the founder's live selected session — no direct invitation-row insert. This closes the last
        //    real-flow seam (council-verdict-2026-07-23T1140Z-pr3122 F1): issue → accept → act → revoke
        //    now runs fully through real authorities. The raw code is returned exactly once here. ──────
        var issued = await h.AdminTeam.IssueInvitationAsync(
            founderSelectedHandle,
            tenantId,
            new[] { Permission.ContactsRead, Permission.SchedulingRead },
            "founder-invite-joiner-2620",
            FounderAuthority(tenantId));
        Assert.NotNull(issued);
        var inviteCode = issued!.Code;

        // ── Step 5: joiner accepts (the REAL #2614 saga → real grant + epoch + membership). ──────────
        var joinerHash = h.Hasher.HashPassword(HashUser, JoinerPassword);
        var accept = await h.Acceptance.AcceptAsync(new AccountSetupAcceptCommand(
            inviteCode, tenantId, JoinerUsername, joinerHash, Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.Accepted, accept.Status);

        // GATE-1 single-use (F2): a SECOND accept with the same code is refused before any mint — the
        // invitation was consumed on first use (ConsumeAndReadAsync is single-use + serializable).
        var replay = await h.Acceptance.AcceptAsync(new AccountSetupAcceptCommand(
            inviteCode, tenantId, JoinerUsername, joinerHash, Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.InvitationRefused, replay.Status);

        // ── Step 6: joiner session — same REAL challenge → select → materialize path. ────────────────
        var joinerSelectedHandle = await LoginAndSelectAsync(h, JoinerUsername, JoinerPassword, tenantId);
        var joinerPrincipal = await h.SelectedSessionPrincipals.AuthenticateAsync(joinerSelectedHandle);
        Assert.NotNull(joinerPrincipal);

        // The two live principals are distinct actors: distinct member Party (the F9 distinction — same
        // node key, distinct signed member_party_id).
        Assert.NotEqual(founderPrincipal.CanonicalParty.Value, joinerPrincipal!.CanonicalParty.Value);

        // ── Step 6b (card 3558): this is the link that made the authorization finding a FACT ────────
        // Everything above is the genuine article — a real invitation issued from the founder's live
        // session, a real acceptance saga, a real login and tenant selection, and `joinerPrincipal`
        // minted by the PRODUCTION WebSelectedSessionPrincipalAuthority from the real session handle.
        // No stub anywhere on that path.
        //
        // The selected-session principal is real, and the composed route reads the live grant through
        // the same PEP resolver used by the production web plane.
        Assert.False(
            string.IsNullOrWhiteSpace(joinerPrincipal.CanonicalParty.Value),
            "the invited member's published principal must carry a real canonical Party.");

        // NOW MAKE THE REQUEST. Real member, really signed in, real session handle, sent as the cookie
        // to a contacts endpoint composed exactly as Program.cs composes it and authenticated through
        // the production selected-session authority. Nothing on this path is stubbed.
        var memberSurface = await SessionSurfaceAsync(
            BankingTeam(h), h.SelectedSessionPrincipals, h.PermissionResolver, h.RouteGate,
            joinerSelectedHandle, includeMutation: false);

        Assert.Contains(Permission.ContactsRead, memberSurface.Permissions);
        Assert.Contains(Permission.ContactsCreate, memberSurface.Permissions);
        Assert.Contains(Permission.SchedulingRead, memberSurface.Permissions);
        Assert.Equal(HttpStatusCode.OK, memberSurface.ContactsRead);
        Assert.Equal(HttpStatusCode.OK, memberSurface.ScheduleRead);
        Assert.Equal(memberSurface.ContactCountBefore, memberSurface.ContactCountAfter);

        var founderSurface = await SessionSurfaceAsync(
            BankingTeam(h), h.SelectedSessionPrincipals, h.PermissionResolver, h.RouteGate,
            founderSelectedHandle, includeMutation: false);
        Assert.Contains(Permission.ContactsCreate, founderSurface.Permissions);
        Assert.Contains(Permission.SchedulingAuthor, founderSurface.Permissions);

        // ── Step 7: BOTH principals perform a real journal-entry write carrying their OWN attribution. ─
        // Each write materialises the attribution from the live request-bound SelectedSessionRequestPrincipal
        // (HttpContext.Features) — the same source the real route seams read.
        await WriteJournalAsAsync(h, founderPrincipal, "JE-FOUNDER-2620", 100m);
        await WriteJournalAsAsync(h, joinerPrincipal, "JE-JOINER-2620", 200m);

        var rows = await h.ReadAuditRowsAsync();
        Assert.Equal(2, rows.Count);
        var members = rows
            .Select(r => ParseAttribution(r.Payload).GetProperty("member_party_id").GetString())
            .ToArray();

        // TOOTH 3 — two DISTINCT member Party attributions, neither collapsed to the operator constant.
        Assert.Contains(FounderParty, members);
        Assert.Contains(joinerPrincipal.CanonicalParty.Value, members);
        Assert.Equal(2, members.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ActiveTeamAuthorizationContext.LocalUserId, members);

        // Attestation integrity — every JE-posted audit row's node signature VERIFIES, and the reader
        // surfaces both as Verified.
        foreach (var row in rows)
        {
            var op = NodeAuditSignaturePayload.TryReconstruct(row, h.Signer.Signer.IssuerId, row.Signature!);
            Assert.NotNull(op);
            Assert.True(h.Verifier.Verify(op!), "the node-signed attribution envelope must verify (attestation integrity)");
        }
        var views = (await h.AuditReader.ListAsync(LedgerTenantId.Value, new NodeAuditEventReaderQuery())).Events;
        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.Equal(NodeAuditSignatureClassifier.Verified, v.SignatureState));

        // Tamper-evidence — swapping the attested member Party in the signed payload (keeping the same
        // node signature/issuer/nonce/timestamp) breaks re-verification. The attestation binds the
        // attribution content; it is NOT re-readable as an attestation of a different member.
        var founderRow = rows.Single(r => ParseAttribution(r.Payload)
            .GetProperty("member_party_id").GetString() == FounderParty);
        var forgedPayload = founderRow.Payload.Replace(FounderParty, "party-mallory", StringComparison.Ordinal);
        Assert.NotEqual(founderRow.Payload, forgedPayload);
        var forged = new SignedOperation<string>(
            Payload: forgedPayload,
            IssuerId: h.Signer.Signer.IssuerId,
            IssuedAt: founderRow.OccurredAt,
            Nonce: NodeAuditSignaturePayload.NonceFor(founderRow.AuditId),
            Signature: Signature.FromBytes(founderRow.Signature!));
        Assert.False(h.Verifier.Verify(forged), "a tampered attribution must break attestation integrity");

        // ── Step 7b: both principals use the REAL banking reconciliation routes. ─────────────────────
        // The route host re-authenticates each REAL selected-session handle, binds that materialized
        // principal onto HttpContext.Features, and maps BankAccountRoutes over the durable node banking
        // repositories. Two separate locked rows therefore prove the actor stamp is per member, while
        // the cross-member unlock proves the holder identity also enforces mutual exclusion.
        await using var bankingRoutes = await h.StartBankingRoutesAsync();
        var founderAccount = await h.SeedBankAccountAsync("bank-founder-3377");
        var joinerAccount = await h.SeedBankAccountAsync("bank-joiner-3377");
        const string founderPeriod = "period-founder-3377";
        const string joinerPeriod = "period-joiner-3377";

        var founderLock = await bankingRoutes.PostAsAsync(
            founderSelectedHandle,
            $"{BankAccountRoutes.RouteBase}/{founderAccount.Value}/reconciliation-state/lock",
            new { periodId = founderPeriod });
        Assert.Equal(HttpStatusCode.OK, founderLock.StatusCode);

        var joinerLock = await bankingRoutes.PostAsAsync(
            joinerSelectedHandle,
            $"{BankAccountRoutes.RouteBase}/{joinerAccount.Value}/reconciliation-state/lock",
            new { periodId = joinerPeriod });
        Assert.Equal(HttpStatusCode.OK, joinerLock.StatusCode);

        var founderReconciliation = await h.Banking.ReconciliationRepo.GetByAccountPeriodAsync(
            h.BankingTenant, founderAccount, new FiscalPeriodId(founderPeriod));
        var joinerReconciliation = await h.Banking.ReconciliationRepo.GetByAccountPeriodAsync(
            h.BankingTenant, joinerAccount, new FiscalPeriodId(joinerPeriod));
        Assert.NotNull(founderReconciliation);
        Assert.NotNull(joinerReconciliation);

        // Member B cannot release member A's lock. Assert the route refusal first, then the durable
        // holder evidence below. Against the former constant-holder implementation this returns 200.
        var crossMemberUnlock = await bankingRoutes.PostAsAsync(
            joinerSelectedHandle,
            $"{BankAccountRoutes.RouteBase}/{founderAccount.Value}/reconciliation-state/unlock",
            new { periodId = founderPeriod });
        Assert.Equal(HttpStatusCode.Conflict, crossMemberUnlock.StatusCode);

        var lockHolders = new[]
        {
            founderReconciliation!.LockedByPrincipalId,
            joinerReconciliation!.LockedByPrincipalId,
        };

        // Two real members produce two durable holder identities; neither collapses to the desktop
        // operator fallback. These positive assertions prevent a negative-only broken-outcome pin.
        Assert.Contains(FounderParty, lockHolders);
        Assert.Contains(joinerPrincipal.CanonicalParty.Value, lockHolders);
        Assert.Equal(2, lockHolders.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ActiveTeamAuthorizationContext.LocalUserId, lockHolders);

        // The refusal is observed in the durable row too: a status-only assertion would pass even if
        // the lock were silently cleared.
        var founderLockAfterRefusal = await h.Banking.ReconciliationRepo.GetByAccountPeriodAsync(
            h.BankingTenant, founderAccount, new FiscalPeriodId(founderPeriod));
        Assert.NotNull(founderLockAfterRefusal);
        Assert.Equal(BankReconciliationLockState.Locked, founderLockAfterRefusal!.LockState);
        Assert.Equal(FounderParty, founderLockAfterRefusal.LockedByPrincipalId);

        // ── Step 7c: both principals use the REAL dynamic-forms routes (#3378). ──────────────────────
        // Same shape as 7b, on the surface whose subject AND owner were a constant computed once in
        // HostedFormsApiEndpoint.StartAsync. The route host binds each member's REAL selected-session
        // principal onto HttpContext.Features and maps the REAL FormDefinitionRoutes + FormsRoutes over
        // the REAL production forms composition, so the durable Owner and the durable instance issuer
        // are produced by the shipped code path, not a harness.
        await using var formsRoutes = await FormsRouteHost.StartAsync(
            BankingTeam(h), h.SelectedSessionPrincipals, h.PermissionResolver);

        const string founderFormId = "founder.intake.3378";
        const string joinerFormId = "joiner.intake.3378";
        var formsTenant = NodeTenant.Resolve(BankingTeam(h));

        // Ticket 151: the immutable Member (no forms:author) is REFUSED
        // by the real gate — 403 before any schema synthesis or persistence. This is the flow production
        // enforces; the suite must exercise it, not bypass it.
        var refusedSave = await formsRoutes.PutAsAsync(
            joinerSelectedHandle, $"{FormDefinitionRoutes.RouteBase}/{joinerFormId}", FormSaveBody());
        Assert.Equal(HttpStatusCode.Forbidden, refusedSave.StatusCode);
        Assert.Null(await formsRoutes.Definitions.GetCurrentPublishedAsync(
            new DefinitionAddress(formsTenant, joinerFormId), CancellationToken.None));

        var founderSave = await formsRoutes.PutAsAsync(
            founderSelectedHandle, $"{FormDefinitionRoutes.RouteBase}/{founderFormId}", FormSaveBody());
        Assert.Equal(HttpStatusCode.OK, founderSave.StatusCode);

        var founderSecondSave = await formsRoutes.PutAsAsync(
            founderSelectedHandle, $"{FormDefinitionRoutes.RouteBase}/{joinerFormId}", FormSaveBody());
        Assert.Equal(HttpStatusCode.OK, founderSecondSave.StatusCode);

        // The DURABLE definition rows, not the 200s — a status-only assert passes either way.
        var founderDefinition = await formsRoutes.Definitions.GetCurrentPublishedAsync(
            new DefinitionAddress(formsTenant, founderFormId), CancellationToken.None);
        var joinerDefinition = await formsRoutes.Definitions.GetCurrentPublishedAsync(
            new DefinitionAddress(formsTenant, joinerFormId), CancellationToken.None);
        Assert.NotNull(founderDefinition);
        Assert.NotNull(joinerDefinition);

        Assert.Equal(FounderParty, founderDefinition!.Owner.Value);
        Assert.Equal(FounderParty, joinerDefinition!.Owner.Value);
        Assert.NotEqual(ActiveTeamAuthorizationContext.LocalUserId, founderDefinition.Owner.Value);

        // The submit half: the capability SUBJECT lands on the instance entity as its creation issuer.
        var founderSubmit = await formsRoutes.PostAsAsync(
            founderSelectedHandle,
            $"{FormsRoutes.RouteBase}/{founderFormId}/submit",
            new { name = "founder applicant", unit = "studio" });
        Assert.Equal(HttpStatusCode.Created, founderSubmit.StatusCode);
        var joinerSubmit = await formsRoutes.PostAsAsync(
            joinerSelectedHandle,
            $"{FormsRoutes.RouteBase}/{joinerFormId}/submit",
            new { name = "joiner applicant", unit = "one-bed" });
        Assert.Equal(HttpStatusCode.Created, joinerSubmit.StatusCode);

        var founderAuthor = await formsRoutes.ReadInstanceAuthorAsync(founderSubmit);
        var joinerAuthor = await formsRoutes.ReadInstanceAuthorAsync(joinerSubmit);
        var issuers = new[] { founderAuthor, joinerAuthor };

        Assert.Contains(FounderParty, issuers);
        Assert.Contains(joinerPrincipal.CanonicalParty.Value, issuers);
        Assert.Equal(2, issuers.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ActiveTeamAuthorizationContext.LocalUserId, issuers);

        // ── Step 8: founder revokes the joiner via the REAL admin authority (grant revocation). ──────
        var joinerGrantId = await h.ResolveJoinerGrantIdAsync(tenantId);
        var revoke = await h.AdminTeam.RevokeMemberGrantAsync(
            founderSelectedHandle, tenantId, joinerGrantId, FounderAuthority(tenantId));
        Assert.NotNull(revoke);
        // TOOTH 2 — the revocation actually revoked (Status + the durable RevokedAtUnixMs).
        Assert.Equal(AdminRevokeMemberStatus.Revoked, revoke!.Status);
        await using (var grants = h.SearchStore.CreateContext())
        {
            var grantRow = await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == joinerGrantId);
            Assert.NotNull(grantRow.RevokedAtUnixMs);
        }

        // ── Step 9: the joiner's next request is refused by the fence on the revoked grant. ──────────
        // TOOTH 1 — AuthenticateAsync re-drives the full session revalidation, whose ResolveUsableMembership
        // hits the REAL LiveTenantMembershipAuthorityAdmission, which refuses the revoked grant → null.
        // Probe the same live immutable-member handle that performed the actions above.
        var joinerAfterRevoke = await h.SelectedSessionPrincipals.AuthenticateAsync(joinerSelectedHandle);
        Assert.Null(joinerAfterRevoke);

        // The founder's own session is untouched by the joiner's revocation (revocation is targeted).
        var founderAfterRevoke = await h.SelectedSessionPrincipals.AuthenticateAsync(founderSelectedHandle);
        Assert.NotNull(founderAfterRevoke);

        // The revoked joiner's lock is an orphan until its bounded lease expires. Advance the
        // banking-only test clock without sleeping, then prove the founder can recover that exact
        // row through the real route. No authorization lookup or database edit clears the holder.
        h.BankingClock.Advance(ReconciliationLockLease.DefaultDuration);
        var recoverOrphanedLock = await bankingRoutes.PostAsAsync(
            founderSelectedHandle,
            $"{BankAccountRoutes.RouteBase}/{joinerAccount.Value}/reconciliation-state/lock",
            new { periodId = joinerPeriod });
        Assert.Equal(HttpStatusCode.OK, recoverOrphanedLock.StatusCode);

        var recoveredReconciliation = await h.Banking.ReconciliationRepo.GetByAccountPeriodAsync(
            h.BankingTenant, joinerAccount, new FiscalPeriodId(joinerPeriod));
        Assert.NotNull(recoveredReconciliation);
        Assert.Equal(FounderParty, recoveredReconciliation!.LockedByPrincipalId);
        Assert.Equal(1, recoveredReconciliation.Version);
    }

    private static async Task<AcceptedMembers> CreateAcceptedMembersAsync(
        PermissionSet? founderRosterPermissions = null)
    {
        var h = await Harness.CreateAsync(founderRosterPermissions);
        var tenantId = Guid.Parse(TeamTenantId).ToString("D");
        var founderHash = h.Hasher.HashPassword(HashUser, FounderPassword);
        var bootstrap = await h.Bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
            FounderUsername,
            founderHash,
            Guid.NewGuid().ToString("N"),
            string.Join(":", Enumerable.Repeat("AB", 32)),
            "founder-bootstrap-3770-guards"));
        Assert.Equal(InstallationFounderBootstrapStatus.Created, bootstrap.Status);
        var founderAccountId = bootstrap.AccountId!;
        var founderIssuance = await h.GrantIssuance.IssueWithEpochAsync(new AdmissionCompleted(
            new TenantId(tenantId),
            new PrincipalUserId(FounderPrincipal),
            new CanonicalPartyReference(FounderParty),
            new PrincipalUserId(FounderPrincipal),
            "founder-genesis-3770-guards", RoleReference.Administrator,
            new GrantProvenance(GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap), new ActorId(FounderPrincipal))));
        var founderMembership = await h.Coordinator.ExecuteAsync(new InstallationIdentityCoordinationCommand(
            CorrelationId: Digest("founder-membership-3770-guards"),
            AccountId: founderAccountId,
            ActorAccountId: founderAccountId,
            AuthorityEvidenceDigest: Digest("founder-evidence-3770-guards"),
            ExpectedAccountOwnerVersion: 1,
            ExpectedAccountSecurityVersion: 1,
            ExpectedActorOwnerVersion: 1,
            ExpectedActorSecurityVersion: 1,
            Mutations:
            [
                new TenantMembershipMutation(
                    TenantId: tenantId,
                    CanonicalPrincipalId: FounderPrincipal,
                    GrantId: founderIssuance.Grant.GrantId.ToString(),
                    ExpectedGrantOwnerVersion: 1,
                    AuthorizationEpoch: founderIssuance.AuthorizationEpoch,
                    ExpectedMembershipOwnerVersion: 0,
                    TargetStatus: TenantMembershipStatus.Active),
            ]));
        Assert.Equal(InstallationIdentityCoordinationStatus.Completed, founderMembership.Status);

        var founderSelectedHandle = await LoginAndSelectAsync(
            h, FounderUsername, FounderPassword, tenantId);
        var issued = await h.AdminTeam.IssueInvitationAsync(
            founderSelectedHandle,
            tenantId,
            new[] { Permission.ContactsRead },
            "founder-invite-3770-guards",
            FounderAuthority(tenantId));
        Assert.NotNull(issued);
        var joinerHash = h.Hasher.HashPassword(HashUser, JoinerPassword);
        var accepted = await h.Acceptance.AcceptAsync(new AccountSetupAcceptCommand(
            issued!.Code,
            tenantId,
            JoinerUsername,
            joinerHash,
            Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.Accepted, accepted.Status);

        return new AcceptedMembers(
            h,
            tenantId,
            founderSelectedHandle,
            issued.InvitationId,
            await h.ResolveJoinerGrantIdAsync(tenantId));
    }

    private sealed record AcceptedMembers(
        Harness Harness,
        string TenantId,
        string FounderSelectedHandle,
        string InvitationId,
        string JoinerGrantId);

    // ── real challenge → select flow (single-membership auto/explicit tenant) ─────────────────────────
    private static async Task<string> LoginAndSelectAsync(
        Harness h, string username, string password, string tenantId)
    {
        var challenge = (await h.ChallengeIssuer.IssueAsync(username, password)).Challenge;
        Assert.NotNull(challenge);
        var selection = await h.TenantSelection.SelectAsync(challenge!.Handle, tenantId);
        Assert.NotNull(selection);
        return selection!.Handle;
    }

    // Enter NodeCallerAttributionScope with the live principal's attribution, then perform a REAL atomic
    // JE + audit write — the same attribution scope the production listener enters (SharedHostedWebApp's
    // selected-session accept branch). This harness drives the identity substrate directly; propagation
    // through the SERVING pipeline is proven by NodeServingPipelineAttributionTests.
    private static async Task WriteJournalAsAsync(
        Harness h, SelectedSessionRequestPrincipal principal, string journalEntryId, decimal amount)
    {
        // Advance ONLY the audit-enlister clock (the identity/session/grant/fence/revoke flow stays on the
        // frozen clock). Two audit rows written at the identity flow's single frozen instant would share an
        // OccurredAt, making the hash-chain tie-break depend on the random AuditId — a non-deterministic
        // verification. Distinct monotonic OccurredAt per write gives a stable chain (the platform's own
        // NodeAttributionEnvelopeTests uses System time on the enlister for the same reason).
        h.AuditClock.Advance(TimeSpan.FromSeconds(1));
        using (NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(principal)))
        {
            // The carried decision is the audit evidence. For a party-authored act its write
            // principal is the canonical Party authenticated at the request boundary, not the
            // installation operator/session actor.
            var authority = new AuthorizationWriteContext(
                new ActorId(principal.CanonicalParty.Value),
                LedgerTenantId,
                h.AuditClock.GetUtcNow());
            await h.JournalStore.SaveAtomicForTestAsync(
                LedgerTenantId,
                BalancedPosted(journalEntryId, amount, authority.At),
                authority);
        }
    }

    private static JsonElement ParseAttribution(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("attribution").Clone();
    }

    private static JournalEntry BalancedPosted(string id, decimal amount, DateTimeOffset at) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LedgerTenantId,
            entryDate: new DateOnly(2026, 7, 23),
            memo: "two-user acceptance E2E",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(Now))
        {
            Status = JournalEntryStatus.Posted,
            PostedAtUtc = new Instant(at),
        };

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // A throwaway user for the Argon2id hasher (its verification ignores the user object; the salt is
    // random and the artifact is user-type-agnostic).
    private static readonly InstallationAccountRecord HashUser = new()
    {
        AccountId = "hash-user",
        NormalizedUsername = "HASH-USER",
        CredentialHash = "n/a",
        CredentialAlgorithm = "n/a",
        CredentialCeremonyId = "n/a",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Active,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    // ── the full REAL-substrate composition ───────────────────────────────────────────────────────────
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<string> _tempFiles = new();
        private readonly List<string> _tempDirs = new();
        private readonly SearchTestStore _searchStore;
        private readonly SqlCipherEncryptedStore _membershipStore;
        private readonly ServiceProvider _ledgerProvider;
        private readonly NodePrincipalSigner _signer;

        private Harness(
            SearchTestStore searchStore,
            SqlCipherEncryptedStore membershipStore,
            ServiceProvider ledgerProvider,
            NodePrincipalSigner signer,
            IPasswordHasher<InstallationAccountRecord> hasher,
            IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
            InstallationFounderBootstrapService bootstrap,
            InitialGrantIssuanceService grantIssuance,
            InstallationIdentityCoordinatorService coordinator,
            IWebFounderBindAuthority founderBind,
            IWebAccountAccessChallengeIssuer challengeIssuer,
            IWebTenantSelectionAuthority tenantSelection,
            IWebSelectedSessionPrincipalAuthority selectedSessionPrincipals,
            IAccountSetupAcceptanceAuthority acceptance,
            IAdminTeamAccessAuthority adminTeam,
            ISelectedSessionPermissionResolver permissionResolver,
            ITenantMembershipAuthorityStore membershipAuthorityStore,
            MutableRosterReader rosterReader,
            NodeEfJournalStore journalStore,
            BankingServices banking,
            IActiveTeamAccessor bankingTeam,
            MutableTimeProvider bankingClock,
            MutableTimeProvider auditClock,
            IOperationVerifier verifier,
            NodeAuditEventReader auditReader,
            List<string> tempFiles,
            List<string> tempDirs)
        {
            _searchStore = searchStore;
            _membershipStore = membershipStore;
            _ledgerProvider = ledgerProvider;
            _signer = signer;
            Hasher = hasher;
            IdentityFactory = identityFactory;
            Bootstrap = bootstrap;
            GrantIssuance = grantIssuance;
            Coordinator = coordinator;
            FounderBind = founderBind;
            ChallengeIssuer = challengeIssuer;
            TenantSelection = tenantSelection;
            SelectedSessionPrincipals = selectedSessionPrincipals;
            Acceptance = acceptance;
            AdminTeam = adminTeam;
            PermissionResolver = permissionResolver;
            MembershipAuthorityStore = membershipAuthorityStore;
            RosterReader = rosterReader;
            JournalStore = journalStore;
            Banking = banking;
            BankingTeam = bankingTeam;
            BankingClock = bankingClock;
            AuditClock = auditClock;
            Verifier = verifier;
            AuditReader = auditReader;
            _tempFiles = tempFiles;
            _tempDirs = tempDirs;
        }

        public IPasswordHasher<InstallationAccountRecord> Hasher { get; }
        public IDbContextFactory<NodeLocalInstallationIdentityDbContext> IdentityFactory { get; }
        public InstallationFounderBootstrapService Bootstrap { get; }
        public InitialGrantIssuanceService GrantIssuance { get; }
        public InstallationIdentityCoordinatorService Coordinator { get; }
        public IWebFounderBindAuthority FounderBind { get; }
        public IWebAccountAccessChallengeIssuer ChallengeIssuer { get; }
        public IWebTenantSelectionAuthority TenantSelection { get; }
        public IWebSelectedSessionPrincipalAuthority SelectedSessionPrincipals { get; }
        public IAccountSetupAcceptanceAuthority Acceptance { get; }
        public IAdminTeamAccessAuthority AdminTeam { get; }
        public ISelectedSessionPermissionResolver PermissionResolver { get; }
        public ITenantMembershipAuthorityStore MembershipAuthorityStore { get; }
        public MutableRosterReader RosterReader { get; }
        public NodeEfJournalStore JournalStore { get; }
        public BankingServices Banking { get; }
        public IActiveTeamAccessor BankingTeam { get; }
        public TenantId BankingTenant => NodeTenant.Resolve(BankingTeam);
        public MutableTimeProvider BankingClock { get; }
        public MutableTimeProvider AuditClock { get; }
        public NodePrincipalSigner Signer => _signer;
        public IOperationVerifier Verifier { get; }
        public NodeAuditEventReader AuditReader { get; }
        public SearchTestStore SearchStore => _searchStore;

        /// <summary>
        /// The REAL <see cref="AuthorizationGate"/> over this harness's live grant store and authorization
        /// definitions — the same pair the production PEP resolves through. Ticket 205 slice 4 routes every
        /// converted route guard through a gate, so the acceptance surface must hand it THIS gate: an
        /// allow-all double there would stop measuring whether an admitted member actually reaches the route.
        /// </summary>
        public AuthorizationGate RouteGate { get; private set; } = default!;

        public static async Task<Harness> CreateAsync(PermissionSet? founderRosterPermissions = null)
        {
            var tempFiles = new List<string>();
            var tempDirs = new List<string>();
            var time = new FixedTimeProvider(Now);
            var canonicalTenantId = Guid.Parse(TeamTenantId).ToString("D");

            // Identity + web-session durable stores (migrated) — the real challenge/select/coordinator
            // substrate. Identity shares the encrypted local-node database with search, as in production.
            var sessionPath = TempFile(tempFiles, "session");
            var searchStore = await SearchTestStore.CreateAsync();
            var identityFactory = searchStore.InstallationIdentityFactory;
            var sessionFactory = ContextFactory<NodeLocalWebSessionDbContext>.Create(
                sessionPath, o => new NodeLocalWebSessionDbContext(o),
                NodeLocalWebSessionDbContext.MigrationsHistoryTableName);
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            // Grants + authorization epochs — the REAL encrypted search store, sharing the production
            // local-node database with the independently migrated installation-identity context above.
            var grantStore = new NodeEfGrantStore(searchStore.Factory);
            var grantIssuance = new InitialGrantIssuanceService(
                grantStore, TestAuthorization.AllowGate(), time);
            var roleVocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
            var authorizationStore = new NodeEfAuthorizationConfigurationStore(
                searchStore.Factory, roleVocabulary);
            var authorizationWriter = new AuthorizationDefinitionWriter(
                authorizationStore,
                authorizationStore,
                new AuthorizationDefinitionAdmission(roleVocabulary),
                new AuthorizationCapabilityBindingAdmission(),
                TestAuthorization.AllowGate(),
                grantStore);
            await new AccessGrantAuthorizationSeed(authorizationWriter, authorizationStore, grantStore)
                .InstallAsync(
                    new TenantId(canonicalTenantId),
                    time.GetUtcNow(),
                    AuthorizationSeedProfile.Production);
            var liveAuthorization = new DefinitionJoinedAuthorizationReader(grantStore, authorizationStore);
            // Ticket 293 slice 4 fix 4 - the REAL gate over the REAL grant store and the installed role
            // definitions. AllowGate() cannot stand in for it any more: the roster supplies no deciding set, so
            // every site that ANDs RequiredPermissions or enumerates InstallRootPermissionsAsync needs the
            // subject's actual conferred atoms. The founder's Administrator grant (issued below through the real
            // InitialGrantIssuanceService) is what admits it, and nothing the roster says.
            var liveGate = new AuthorizationGate(
                liveAuthorization, new EmptyRecordStandingResolver(), authorizationStore);

            // Seeded-fixture party reader (recipe-permitted): the founder principal maps to the roster's
            // admin party; every other principal (the joiner) resolves to a distinct generated Party.
            var partyReader = new MapPartyReader(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FounderPrincipal] = FounderParty,
            });

            // Tooth 1 — the REAL grant-anchored fence.
            var admission = new LiveTenantMembershipAuthorityAdmission(partyReader, searchStore.Factory, time);

            // REAL encrypted tenant-membership partition, resolved by a fixture resolver (the ONLY infra
            // substitution — the membership STORE is the real EncryptedTenantMembershipAuthorityStore).
            var membershipDbPath = TempFile(tempFiles, "membership");
            var membershipStore = new SqlCipherEncryptedStore();
            await membershipStore.OpenAsync(
                membershipDbPath, RandomNumberGenerator.GetBytes(32), CancellationToken.None);
            var membershipAuthorityStore = new EncryptedTenantMembershipAuthorityStore(
                membershipStore,
                canonicalTenantId,
                new InstallationIdentityHomeDecisionAuthority(identityFactory),
                time);
            var partition = new TenantIdentityAuthorityPartition(
                canonicalTenantId, membershipAuthorityStore, new AlwaysLeaseCoordinator());
            var partitionResolver = new FixturePartitionResolver(partition);

            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory, partitionResolver, admission, time, TestAuthorization.Gate(true), grantStore);
            var candidateLocator = new InstallationTenantCandidateLocator(identityFactory, coordinator);

            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            var founderBind = new WebFounderBindAuthority(identityFactory, time);

            var hasher = new Argon2idPasswordHasher<InstallationAccountRecord>(
                Options.Create(new Argon2idHashOptions()));
            var sessionOptions = Options.Create(new Harborline.Api.Foundation.Session.SessionOptions());

            var challengeIssuer = new WebAccountAccessChallengeIssuer(
                identityFactory, sessionFactory, hasher, FixtureV1AuthorityGate.Admitting, time);
            var tenantSelection = new WebTenantSelectionAuthority(
                identityFactory, sessionFactory, candidateLocator, coordinator, partitionResolver,
                partyReader, FixtureV1AuthorityGate.Admitting, sessionOptions, time);
            var selectedSessionStore = new WebSelectedSessionStore(sessionFactory);
            var selectedSessionPrincipals = new WebSelectedSessionPrincipalAuthority(
                selectedSessionStore, identityFactory, coordinator, partyReader, sessionOptions, time);

            // A REAL Ed25519-signed roster with the founder admitted as members:manage admin (the roster is
            // not one of the three teeth; a FixedRosterReader over a REAL signed roster mirrors the
            // platform's own AdminTeamAccessAuthorityTests).
            var rosterReader = BuildFounderAdminRoster(
                Guid.Parse(TeamTenantId), founderRosterPermissions ?? PermissionCompositions.Admin);
            var permissionResolver = new SelectedSessionPermissionResolver(
                rosterReader,
                grantStore,
                new NodeSelectedSessionAuthorizationEpochReader(searchStore.Factory),
                time,
                NullLogger<SelectedSessionPermissionResolver>.Instance, liveGate);

            // The #2614-blessed acceptance-saga fakes (copied from AccountSetupAcceptanceServiceTests).
            var invitationStore = new AccountSetupInvitationStore(identityFactory);
            var invitationIssuer = new AccountSetupInvitationIssuer(
                sessionFactory, selectedSessionStore, identityFactory, searchStore.Factory,
                partyReader, rosterReader,
                invitationStore, liveGate, time);
            var granterAuthority = liveAuthorization;
            var partyBindingMinter = new RecordingPartyBindingMinter();

            var minterServices = new ServiceCollection();
            minterServices.AddSingleton<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(identityFactory);
            minterServices.AddFrozenKernelClock(time);
            minterServices.AddTransient<WebJoinerAccountMinter>();
            var minterProvider = minterServices.BuildServiceProvider();

            var acceptance = new AccountSetupAcceptanceService(
                invitationStore, granterAuthority, partyBindingMinter, grantIssuance, coordinator,
                minterProvider.GetRequiredService<IServiceScopeFactory>(), time);

            // Tooth 2 — the REAL admin authority (its RevokeMemberGrantAsync is the revocation lever).
            var adminTeam = new AdminTeamAccessAuthority(
                sessionFactory, selectedSessionStore, identityFactory, searchStore.Factory,
                partyReader, rosterReader,
                invitationStore, invitationIssuer, grantStore,
                new AuthorizedGrantRevocationWriter(grantStore), liveAuthorization,
                liveGate, time, new NoopRosterMemberRevocationAuthority(),
                new Harborline.Api.Kernel.Audit.InMemoryAuditTrail(), new Ed25519Signer(KeyPair.Generate()),
                partitionResolver, coordinator);

            // Tooth 3 — the REAL node journal store + node-signed attribution envelope materialised from
            // the live HttpContext-bound principal.
            var ledgerDir = TempDir(tempDirs, "ledger");
            var ledgerConnection = $"Data Source={Path.Combine(ledgerDir, "local-node.db")};Pooling=False";
            var ledgerServices = new ServiceCollection();
            ledgerServices.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
            ledgerServices.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
            ledgerServices.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
            ledgerServices.AddSingleton<IHarborlineEntityModule, BankingEntityModule>();
            ledgerServices.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(ledgerConnection));
            ledgerServices.AddSingleton<IJournalStore, InMemoryJournalStore>();
            var bankingClock = new MutableTimeProvider(Now);
            ledgerServices.AddFrozenKernelClock(bankingClock);
            ledgerServices.AddSingleton(TestAuthorization.AllowGate());
            ledgerServices.AddNodeBankingWrites();

            var feedConnection = $"Data Source={Path.Combine(ledgerDir, "bank-feed.db")};Pooling=False";
            ledgerServices.AddDbContextFactory<NodeLocalBankFeedDbContext>(opt =>
                opt.UseSqlite(feedConnection, sqlite =>
                    sqlite.MigrationsHistoryTable(NodeLocalBankFeedDbContext.MigrationsHistoryTableName)));

            var ledgerProvider = ledgerServices.BuildServiceProvider();
            var ledgerFactory = ledgerProvider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var ledger = await ledgerFactory.CreateDbContextAsync())
            {
                await ledger.Database.EnsureCreatedAsync();
            }
            var feedFactory = ledgerProvider.GetRequiredService<IDbContextFactory<NodeLocalBankFeedDbContext>>();
            await using (var feed = await feedFactory.CreateDbContextAsync())
            {
                await feed.Database.EnsureCreatedAsync();
            }
            var banking = (
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository>(),
                ledgerProvider.GetRequiredService<NodeBankAccountWriter>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IStatementLineRepository>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IReconciliationRepository>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Import.ImportPipelineService>(),
                ledgerProvider.GetRequiredService<AcceptMatchService>(),
                ledgerProvider.GetRequiredService<UnMatchService>(),
                ledgerProvider.GetRequiredService<ReconciliationLockLease>(),
                ledgerProvider.GetRequiredService<Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider>(),
                ledgerProvider.GetRequiredService<IDbContextFactory<NodeLocalBankFeedDbContext>>());
            var bankingTeam = new FixedActiveTeamAccessor(Guid.Parse(TeamTenantId));

            var signer = new NodePrincipalSigner(FixedSeed());
            var auditClock = new MutableTimeProvider(Now);
            var enlister = new NodeAuditWriteEnlister(signer.Signer);
            var journalStore = new NodeEfJournalStore(ledgerFactory, NodeJournalWriteAdapters.Create(audit: enlister));

            var verifier = new Ed25519Verifier();
            var auditReader = new NodeAuditEventReader(
                ledgerFactory, new NodeAuditSignatureVerificationContext(signer.Signer.IssuerId, verifier));

            return new Harness(
                searchStore, membershipStore, ledgerProvider, signer, hasher, identityFactory, bootstrap,
                grantIssuance, coordinator, founderBind, challengeIssuer, tenantSelection,
                selectedSessionPrincipals, acceptance, adminTeam, permissionResolver, membershipAuthorityStore,
                rosterReader,
                journalStore,
                banking, bankingTeam,
                bankingClock, auditClock, verifier, auditReader, tempFiles, tempDirs)
            {
                RouteGate = liveGate,
            };
        }

        public async Task<BankAccountId> SeedBankAccountAsync(string accountId)
        {
            var id = new BankAccountId(accountId);
            var now = new Instant(Now);
            await Banking.AccountWriter.CreateAsync(new BankAccount(
                Id: id,
                TenantId: BankingTenant,
                EntityId: new EntityId("harborline", BankingTenant.Value, accountId),
                Kind: BankAccountKind.Bank,
                DisplayName: accountId,
                InstitutionName: "Test Bank",
                Currency: new CurrencyCode("USD"),
                LinkedLedgerAccount: new LedgerAccountRef(
                    new GLAccountId("1000"),
                    new ChartOfAccountsId("chart-3377")),
                OpeningBalance: 0m,
                CutoverAsOf: now,
                ArchivedAt: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now),
                new AuthorizationWriteContext(new ActorId("mtw2-bank-seed"), BankingTenant, Now));
            return id;
        }

        public Task<BankingRouteHost> StartBankingRoutesAsync() =>
            BankingRouteHost.StartAsync(Banking, BankingTeam, SelectedSessionPrincipals);

        // The joiner's grant is the live grant whose principal is NOT the founder's and not one the installer
        // seeded (the "sys." system principals, and the desktop node operator's workshop:unlock holding) —
        // read from the REAL search store, in the "D" Guid form RevokeMemberGrantAsync parses.
        public async Task<string> ResolveJoinerGrantIdAsync(string tenantId)
        {
            await using var grants = SearchStore.CreateContext();
            var grantId = await grants.Grants.AsNoTracking()
                .Where(g => g.TenantId == tenantId && g.SubjectId != FounderPrincipal &&
                    g.SubjectId != AccessGrantAuthorizationSeed.NodeOperatorPrincipal &&
                    !g.SubjectId.StartsWith("sys."))
                .Select(g => g.GrantId)
                .SingleAsync();
            return grantId;
        }

        public async Task<string> ResolveFounderGrantIdAsync(string tenantId)
        {
            await using var grants = SearchStore.CreateContext();
            return await grants.Grants.AsNoTracking()
                .Where(g => g.TenantId == tenantId && g.SubjectId == FounderPrincipal)
                .Select(g => g.GrantId)
                .SingleAsync();
        }

        public async Task<GrantRow> ReadGrantRowAsync(string grantId)
        {
            await using var grants = SearchStore.CreateContext();
            return await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == grantId);
        }

        public Task AdmitInvitationTargetToRosterAsync(string invitationId)
        {
            var principal = InstallationAuditIntegrity.Hash(
                "web-tenant-principal/v1", TeamTenantId, invitationId);
            var party = $"party-{principal[..8]}";
            using var key = KeyPair.Generate();
            RosterReader.Admit(party, key.PrincipalId, PermissionSet.Of(Permission.ContactsRead));
            return Task.CompletedTask;
        }

        private async Task<InstallationAccountRecord> AccountAsync(string username)
        {
            await using var identity = await IdentityFactory.CreateDbContextAsync();
            return await identity.Accounts.AsNoTracking()
                .SingleAsync(row => row.NormalizedUsername == username.ToUpperInvariant());
        }

        public async Task<List<NodeAuditEventRow>> ReadAuditRowsAsync()
        {
            var ledgerFactory = _ledgerProvider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using var ctx = await ledgerFactory.CreateDbContextAsync();
            return await ctx.Set<NodeAuditEventRow>()
                .Where(r => r.TenantId == LedgerTenantId.Value)
                .OrderBy(r => r.OccurredAt).ThenBy(r => r.AuditId)
                .ToListAsync();
        }

        private static MutableRosterReader BuildFounderAdminRoster(
            Guid tenantId,
            PermissionSet founderPermissions)
        {
            var genesisKey = KeyPair.Generate();
            var founderRosterKey = KeyPair.Generate();
            var genesisSigner = new Ed25519Signer(genesisKey);
            var founderSigner = new Ed25519Signer(founderRosterKey);
            var verifier = new Ed25519Verifier();
            var roster = MemberRoster.Genesis(
                tenantId, "party-genesis-owner", genesisSigner, verifier, Now,
                Guid.Parse("2620aaaa-0000-0000-0000-000000000001"));
            roster = roster.Admit(
                "party-genesis-owner", genesisSigner, FounderParty, founderRosterKey.PrincipalId,
                founderPermissions, verifier, Now,
                Guid.Parse("2620aaaa-0000-0000-0000-000000000002"));
            return new MutableRosterReader(roster, founderSigner, verifier);
        }

        private static byte[] FixedSeed()
        {
            var seed = new byte[32];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = (byte)(0x26 + i);
            }
            return seed;
        }

        private static string TempFile(List<string> sink, string kind)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mtw2-2620-{kind}-{Guid.NewGuid():N}.db");
            sink.Add(path);
            return path;
        }

        private static string TempDir(List<string> sink, string kind)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mtw2-2620-{kind}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            sink.Add(dir);
            return dir;
        }

        public async ValueTask DisposeAsync()
        {
            _signer.Dispose();
            await _ledgerProvider.DisposeAsync();
            await _membershipStore.DisposeAsync();
            await _searchStore.DisposeAsync();
            foreach (var file in _tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { /* best-effort */ }
            }
            foreach (var dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>The harness's active team, reused as the forms tenant anchor (tenant scoping is MTW-3).</summary>
    private static IActiveTeamAccessor BankingTeam(Harness h) => h.BankingTeam;

    /// <summary>
    /// The authoring save body — the same shape the Harborline App's <c>toSaveRequest</c> emits, trimmed to the
    /// two fields these assertions need.
    /// </summary>
    private static object FormSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = FormText("Applicant"), controlHint = "text", piiSensitivity = "None" },
                ["unit"] = new { label = FormText("Unit type"), controlHint = "select", piiSensitivity = "None" },
            },
            sections = new[]
            {
                new
                {
                    id = "applicant",
                    title = FormText("Applicant"),
                    fields = new[] { "name", "unit" },
                    layout = new { kind = "stack" },
                    fieldPlacement = new Dictionary<string, object>(),
                },
            },
            rules = Array.Empty<object>(),
            title = FormText("MTW-2 3378 intake"),
            description = FormText("Two real members author and submit."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
            ["unit"] = new { type = "select", required = false, options = new[] { "studio", "one-bed" } },
        },
    };

    private static object FormText(string en) =>
        new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = en } };

    /// <summary>
    /// Real in-process dynamic-forms route host (#3378). Identical middleware to
    /// <see cref="BankingRouteHost"/> — the test-only header carries an already-issued selected-session
    /// handle, re-authenticated through the real authority and published onto the same
    /// <c>HttpContext.Features</c> seam production uses — over the REAL production forms composition
    /// (<c>AddNodeForms</c>) and the REAL <see cref="FormDefinitionRoutes"/> + <see cref="FormsRoutes"/>.
    /// </summary>
    /// <summary>
    /// Card 3558's second request, made for real: a genuinely invited member, genuinely signed in,
    /// requesting a genuine product data route through the PRODUCTION composition.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the shape <see cref="FormsRouteHost"/> uses. That one maps route families onto
    /// the test's own app, outside the group the hosted endpoint puts them in, and its own comment says
    /// it therefore proves attribution and nothing about reachability. This host composes
    /// <see cref="SharedHostedWebApp"/> and <see cref="HostedContactApiEndpoint"/> exactly as
    /// <c>Program.cs</c> does, and authenticates through the REAL
    /// <see cref="IWebSelectedSessionPrincipalAuthority"/> the harness built — so what it measures is
    /// what the shipped node does.
    /// </remarks>
    private sealed class SurfaceIdentityAuthority : IWebSelectedSessionIdentityAuthority
    {
        public Task<SelectedSessionIdentity?> DescribeAsync(
            string? selectedHandle,
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedSessionIdentity?>(new SelectedSessionIdentity(
                principal.AccountId,
                principal.CanonicalParty,
                null,
                principal.TenantId,
                principal.TenantId.Value,
                SelectedSessionMembership.Member,
                DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private sealed class SurfaceCurrentUser : ICurrentUser
    {
        public string UserId => "mtw2-two-user-surface";
        public IReadOnlyList<string> Roles => Array.Empty<string>();
    }

    private sealed record SessionSurface(
        HttpStatusCode Whoami,
        string[] Permissions,
        HttpStatusCode ContactsRead,
        HttpStatusCode ContactCreate,
        HttpStatusCode ScheduleRead,
        HttpStatusCode ScheduleAuthor,
        int ContactCountBefore,
        int ContactCountAfter);

    private static async Task<SessionSurface> SessionSurfaceAsync(
        IActiveTeamAccessor activeTeam,
        IWebSelectedSessionPrincipalAuthority selectedSessionPrincipals,
        ISelectedSessionPermissionResolver permissionResolver,
        AuthorizationGate routeGate,
        string selectedHandle,
        bool includeMutation)
    {
        const string callerToken = "mtw2-contacts-reachability-caller-token";
        var databasePath = Path.Combine(Path.GetTempPath(), $"mtw2-contacts-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging(b => b.ClearProviders());
        services.AddDbContextFactory<LocalNodeDbContext>(
            o => o.UseSqlite(SqliteTestDatabase.ConnectionString(databasePath)));
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddSingleton(activeTeam);
        services.AddSingleton<Harborline.Api.Foundation.MultiTenancy.ITenantContext, ActiveTeamTenantContext>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        services.AddSingleton(new NodeCallerSessionToken(callerToken));
        services.AddSingleton(selectedSessionPrincipals);
        services.AddSingleton(permissionResolver);
        // Ticket 205 slice 4: the contact and scheduling READ guards on this surface resolve at the gate.
        // The gate is the harness's REAL one, over the live grant store and the seeded definitions, so the
        // OK assertions below still measure the whole path — an admitted member's own grant is what carries
        // the request through the guard. A double here would make those 200s meaningless.
        services.AddSingleton(routeGate);
        // Production's contact audit composes both EF contexts over the same local-node database and
        // enlists the identity audit append in the party transaction. Preserve that atomic store shape.
        var identityDatabasePath = databasePath;
        services.AddDbContextFactory<NodeLocalInstallationIdentityDbContext>(
            o => o.UseSqlite(SqliteTestDatabase.ConnectionString(identityDatabasePath)));
        Harborline.Api.LocalNodeHost.Data.People.NodePeopleComposition.AddNodeContacts(services);
        var schedulingDatabasePath = Path.Combine(Path.GetTempPath(), $"mtw2-scheduling-{Guid.NewGuid():N}.db");
        services.AddDbContextFactory<NodeLocalSchedulingDbContext>(
            o => o.UseSqlite(SqliteTestDatabase.ConnectionString(schedulingDatabasePath)));

        await using var provider = services.BuildServiceProvider();
        await using (var db = await provider
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        await using (var db = await provider
            .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>().CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }
        await InstallationIdentityTestBootstrap.BootstrapAsync(
            provider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>());
        await using (var db = await provider
            .GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>().CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        var app = new SharedHostedWebApp(
            provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            provider.GetRequiredService<TimeProvider>());
        var endpoint = new HostedContactApiEndpoint(
            app,
            provider.GetRequiredService<Harborline.Api.LocalNodeHost.Data.People.NodeEfPartyRepository>(),
            provider.GetRequiredService<Harborline.Api.LocalNodeHost.Data.People.ContactCrdtProjection>(),
            activeTeam,
            provider.GetRequiredService<ILogger<HostedContactApiEndpoint>>(),
            provider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
            provider.GetRequiredService<TimeProvider>());
        await endpoint.StartAsync(CancellationToken.None);
        var schedulingStore = new NodeSchedulingDraftStore(
            provider.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>(),
            TimeProvider.System);
        app.MapApiRoutes(routes =>
        {
            SelectedSessionIdentityRoutes.Map(
                routes.MapSelectedSessionProductGroup(),
                new SurfaceIdentityAuthority());
            SchedulingDefinitionRoutes.Map(
                routes.MapDeviceReachableProductDataGroup(),
                schedulingStore, new SchedulingDraftValidator(), null!, activeTeam,
                new SurfaceCurrentUser(), null!, null!, null!, null!, null!, TimeProvider.System);
        });
        await app.StartAsync(CancellationToken.None);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            using var whoamiRequest = new HttpRequestMessage(HttpMethod.Get, SelectedSessionIdentityRoutes.WhoamiPath);
            whoamiRequest.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
            using var whoami = await client.SendAsync(whoamiRequest);
            var permissions = whoami.IsSuccessStatusCode
                ? (await whoami.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("permissions").EnumerateArray().Select(item => item.GetString()!).ToArray()
                : Array.Empty<string>();

            using var readRequest = new HttpRequestMessage(HttpMethod.Get, ContactRoutes.RouteBase);
            readRequest.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
            using var contactsRead = await client.SendAsync(readRequest);
            var contactCountBefore = await CountPartiesAsync(provider);
            var contactCreate = HttpStatusCode.NotImplemented;
            var scheduleAuthor = HttpStatusCode.NotImplemented;
            if (includeMutation)
            {
                using var createRequest = new HttpRequestMessage(HttpMethod.Post, ContactRoutes.RouteBase)
                {
                    Content = JsonContent.Create(new { displayName = "two-user surface contact", kind = "person" }),
                };
                createRequest.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
                using var createResponse = await client.SendAsync(createRequest);
                contactCreate = createResponse.StatusCode;

                using var authorRequest = new HttpRequestMessage(
                    HttpMethod.Put, "/api/local-node/scheduling/definitions/two-user-surface/draft")
                {
                    Content = JsonContent.Create(new
                    {
                        expectedRevision = 0,
                        definition = new { title = "two-user surface definition" },
                    }),
                };
                authorRequest.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
                using var authorResponse = await client.SendAsync(authorRequest);
                scheduleAuthor = authorResponse.StatusCode;
            }

            var contactCountAfter = await CountPartiesAsync(provider);
            using var scheduleReadRequest = new HttpRequestMessage(
                HttpMethod.Get, SchedulingDefinitionRoutes.RouteBase);
            scheduleReadRequest.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
            using var scheduleRead = await client.SendAsync(scheduleReadRequest);

            return new SessionSurface(
                whoami.StatusCode, permissions, contactsRead.StatusCode, contactCreate,
                scheduleRead.StatusCode, scheduleAuthor, contactCountBefore, contactCountAfter);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            SqliteTestDatabase.Delete(databasePath, identityDatabasePath, schedulingDatabasePath);
        }
    }

    private static async Task<int> CountPartiesAsync(ServiceProvider provider)
    {
        await using var context = await provider
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContextAsync();
        return await context.Set<Party>().CountAsync();
    }

    private sealed class FormsRouteHost : IAsyncDisposable
    {
        private const string SelectedSessionHeader = "X-Mtw2-Selected-Session";
        private readonly WebApplication _app;

        private FormsRouteHost(
            WebApplication app, HttpClient client, IFormDefinitionStore definitions, IVersionStore versions)
        {
            _app = app;
            Client = client;
            Definitions = definitions;
            _versions = versions;
        }

        private readonly IVersionStore _versions;

        private HttpClient Client { get; }

        public IFormDefinitionStore Definitions { get; }

        public static async Task<FormsRouteHost> StartAsync(
            IActiveTeamAccessor activeTeam,
            IWebSelectedSessionPrincipalAuthority selectedSessionPrincipals,
            ISelectedSessionPermissionResolver permissionResolver)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddTestKernelClock();
            // The field encryptor the recovery coordinator supplies in production (INV-S3), so PII-capable
            // definitions round-trip. Mirrors FormsRouteTests' composition exactly.
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            builder.Services.AddTestAuthorizationGate();
            builder.Services.AddTestNodeForms();
            // Ticket 151: FormDefinitionRoutes gates authoring on forms:author. This host resolves the
            // decision the PRODUCTION way — the request-scoped SelectedSessionTenantContext bound to the
            // re-authenticated selected-session principal, whose permission set comes from the REAL
            // per-request PEP resolver (the same live-grant/roster path the production web plane reads).
            // No grant-all: the suite exercises the new gate, it does not bypass it.
            builder.Services.AddSingleton(permissionResolver);
            builder.Services.AddScoped<SelectedSessionTenantContext>();
            builder.Services.AddScoped<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(
                sp => sp.GetRequiredService<SelectedSessionTenantContext>());
            // Ticket 205 slice 4: the DEFINITION-AUTHORING routes' forms:author guard resolves at the gate
            // now, so this host's gate follows the SAME request-scoped selected-session context on that
            // path — the immutable Member is still refused by the real per-request permission set rather
            // than by a harness opinion. Every other decision (the forms ENGINE's own issuance check on the
            // submit path, which this slice does not touch) keeps the allow-all posture
            // AddTestAuthorizationGate gave it, so no unrelated tooth moves. A SINGLETON reading the live
            // request scope: the forms composition resolves the gate from the root provider, so a scoped
            // gate cannot be built here.
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(sp => Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(permission =>
            {
                var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
                if (http is null
                    || !http.Request.Path.StartsWithSegments(FormDefinitionRoutes.RouteBase))
                    return true;
                return http.RequestServices
                    .GetRequiredService<Harborline.Api.Foundation.Authorization.IAuthorizationContext>()
                    .HasPermission(permission);
            }));
            var app = builder.Build();

            app.Use(async (HttpContext http, RequestDelegate next) =>
            {
                var selectedHandle = http.Request.Headers[SelectedSessionHeader].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(selectedHandle))
                {
                    var principal = await selectedSessionPrincipals.AuthenticateAsync(selectedHandle);
                    if (principal is not null)
                    {
                        http.Features.Set(principal);
                        // The same bind-once production performs per request: the scoped authorization
                        // facade resolves the principal's REAL permission set before any handler runs.
                        await http.RequestServices
                            .GetRequiredService<SelectedSessionTenantContext>()
                            .BindAsync(principal);
                    }
                }

                await next(http);
            });

            // ATTRIBUTION + PER-MEMBER AUTHORIZATION -- THIS DOES NOT PROVE REACHABILITY (card #3367).
            //
            // These two families are mapped onto this test's OWN `app`, deliberately OUTSIDE the
            // desktop-plane-only group HostedFormsApiEndpoint puts them in. So the joiner assertions
            // below prove the identity half -- distinct definition owners, distinct instance authors,
            // which is the ONLY pin on the per-request resolution #3437 landed -- plus, since ticket
            // 151, the AUTHORIZATION half of the definition-authoring gate (forms:author resolved from
            // the member's real grant through the production PEP resolver). They still prove NOTHING
            // about whether a member can reach these routes in production.
            //
            // In production after #3367 the joiner gets 403 web-plane.route.unavailable for every call
            // asserted here, and will until MTW-3 makes per-member authorization real. A green
            // "two user acceptance" that contradicts the shipped behaviour is exactly the false-
            // readiness signal ADR 0160 R3-I.9 exists to prevent, so it is named here rather than left
            // for a reader to discover. Do not delete this test to resolve the tension: it is the only
            // thing holding the identity half in place.
            FormDefinitionRoutes.Map(
                app,
                app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
                app.Services.GetRequiredService<ISchemaRegistry>(),
                activeTeam,
                TimeProvider.System);
            FormsRoutes.Map(
                app,
                app.Services.GetRequiredService<IFormEngine>(),
                app.Services.GetRequiredService<IFormCapabilityIssuer>(),
                app.Services.GetRequiredService<IFormCapabilityVerifier>(),
                activeTeam,
                new[] { FormsRoutes.NodeOperatorRole },
                TimeProvider.System);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new FormsRouteHost(
                app,
                client,
                app.Services.GetRequiredService<IFormDefinitionStore>(),
                app.Services.GetRequiredService<IVersionStore>());
        }

        public Task<HttpResponseMessage> PutAsAsync(string selectedSessionHandle, string route, object body) =>
            SendAsAsync(HttpMethod.Put, selectedSessionHandle, route, body);

        public Task<HttpResponseMessage> PostAsAsync(string selectedSessionHandle, string route, object body) =>
            SendAsAsync(HttpMethod.Post, selectedSessionHandle, route, body);

        /// <summary>
        /// The durable AUTHOR of the instance version a submit minted. <c>FormEngine</c> passes the
        /// capability token's subject as <c>CreateOptions.Issuer</c>, which the entity store records as
        /// the creating version's <c>Author</c> — so this reads the stamp itself, not a status code.
        /// </summary>
        public async Task<string> ReadInstanceAuthorAsync(HttpResponseMessage submitResponse)
        {
            var body = await submitResponse.Content.ReadFromJsonAsync<JsonElement>();
            var instanceId = body.GetProperty("instanceId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(instanceId));
            Assert.True(EntityId.TryParse(instanceId!, out var parsed));

            var authors = new List<string>();
            await foreach (var version in _versions.GetHistoryAsync(parsed, CancellationToken.None))
            {
                authors.Add(version.Author.Value);
            }
            return Assert.Single(authors.Distinct(StringComparer.Ordinal));
        }

        private Task<HttpResponseMessage> SendAsAsync(
            HttpMethod method, string selectedSessionHandle, string route, object body)
        {
            var request = new HttpRequestMessage(method, route)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add(SelectedSessionHeader, selectedSessionHandle);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Real in-process banking route host. The test-only header carries an already-issued selected-session
    /// handle; middleware re-authenticates it through the real authority before publishing the principal
    /// onto the same HttpContext.Features seam production uses.
    /// </summary>
    private sealed class BankingRouteHost : IAsyncDisposable
    {
        private const string SelectedSessionHeader = "X-Mtw2-Selected-Session";
        private readonly WebApplication _app;

        private BankingRouteHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        private HttpClient Client { get; }

        public static async Task<BankingRouteHost> StartAsync(
            BankingServices banking,
            IActiveTeamAccessor activeTeam,
            IWebSelectedSessionPrincipalAuthority selectedSessionPrincipals)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.Use(async (HttpContext http, RequestDelegate next) =>
            {
                var selectedHandle = http.Request.Headers[SelectedSessionHeader].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(selectedHandle))
                {
                    var principal = await selectedSessionPrincipals.AuthenticateAsync(selectedHandle);
                    if (principal is not null)
                    {
                        http.Features.Set(principal);
                    }
                }

                await next(http);
            });

            BankAccountRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                (banking.AccountRepo, banking.LineRepo, banking.LinkRepo, banking.ReconciliationRepo,
                    banking.PeriodRepo, banking.ImportPipeline, banking.AcceptMatchService, banking.UnMatchService,
                    banking.ReconciliationLease, banking.FeedProvider, banking.FeedConnectionFactory),
                activeTeam,
                banking.AccountWriter,
                TimeProvider.System);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new BankingRouteHost(app, client);
        }

        public Task<HttpResponseMessage> PostAsAsync(string selectedSessionHandle, string route, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add(SelectedSessionHeader, selectedSessionHandle);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class FixedActiveTeamAccessor : IActiveTeamAccessor
    {
        public FixedActiveTeamAccessor(Guid teamId)
        {
            Active = new TeamContext(
                new TeamId(teamId),
                "MTW-2 acceptance team",
                new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        }

        public TeamContext? Active { get; }

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;

        private void KeepEvent() =>
            ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(Active, Active));
    }

    // ── substituted (recipe-listed / not-a-tooth) collaborators ──────────────────────────────────────
    private sealed class MapPartyReader(IReadOnlyDictionary<string, string> principalToParty)
        : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default)
        {
            var party = principalToParty.TryGetValue(user.Value, out var mapped)
                ? mapped
                : "party-" + user.Value[..8];
            return ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(party)));
        }
    }

    private sealed class MutableRosterReader(
        MemberRoster roster,
        IOperationSigner founderSigner,
        IOperationVerifier verifier) : IVerifiedTenantRosterReader
    {
        private MemberRoster _roster = roster;

        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) =>
            Task.FromResult(_roster);

        public void Admit(string partyId, PrincipalId publicKey, PermissionSet permissions)
        {
            _roster = _roster.Admit(
                FounderParty,
                founderSigner,
                partyId,
                publicKey,
                permissions,
                verifier,
                Now,
                Guid.NewGuid());
        }
    }

    private sealed class FixedRosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) => Task.FromResult(roster);
    }

    private sealed class RecordingPartyBindingMinter : IWebJoinerPartyBindingMinter
    {
        public Task<CanonicalPartyReference> MintAsync(
            TenantId tenant, PrincipalUserId joinerPrincipal, PartyId actor,
            string displayName, DateTimeOffset admittedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalPartyReference($"party-{joinerPrincipal.Value[..8]}"));
    }

    private sealed class FixturePartitionResolver(TenantIdentityAuthorityPartition partition)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(string tenantId, CancellationToken ct)
        {
            if (!string.Equals(partition.TenantId, tenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected tenant partition requested: '{tenantId}'.");
            }
            return Task.FromResult(partition);
        }
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(
                Guid.NewGuid().ToString("N"), resourceId, "test-node", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) => _held.Values.Any(l => l.ResourceId == resourceId);
        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContextFactory<TContext>(
        string path,
        Func<DbContextOptions<TContext>, TContext> create,
        string historyTable) : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public static ContextFactory<TContext> Create(
            string path, Func<DbContextOptions<TContext>, TContext> create, string historyTable) =>
            new(path, create, historyTable);

        public TContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseSqlite($"Data Source={path};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(historyTable))
                .Options;
            return create(options);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A monotonic clock used ONLY by the audit enlister so successive JE-posted audit rows get distinct,
    // increasing OccurredAt values (a stable hash chain). The identity/session/grant/fence/revoke flow is
    // on the frozen FixedTimeProvider.
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
