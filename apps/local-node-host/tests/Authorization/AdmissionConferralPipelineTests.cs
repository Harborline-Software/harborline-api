using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// DES-0029 kernel-core-ck-10 at the admission conferral: the conferral runs authorize, bind, mutate, validate,
/// commit and react in ADR 0038 order inside its own fence, and a refusal at any stage (including after the
/// commit stage has saved) persists no definition, role, grant or audit entry. Real keyed search store; the
/// commit and crash faults are SQLite triggers on the real tables.
/// </summary>
public sealed class AdmissionConferralPipelineTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-ck10-conferral");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    private const string Admitter = "founder-ck10";
    private const string Admitted = "party-ck10";
    private static readonly PermissionSet Permissions =
        PermissionSet.From([Permission.OrgManageSettings, Permission.ContactsRead]);

    private static readonly WritePipelineStage[] Adr0038 =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    private SearchTestStore _store = null!;

    public async Task InitializeAsync() => _store = await SearchTestStore.CreateAsync();

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact(DisplayName = "ck-10 conferral: every stage runs in ADR 0038 order and commits the definitions, grant and audit together")]
    public async Task Conferral_RunsEveryStageAndCommitsDefinitionsGrantAndAuditTogether()
    {
        var recorder = new StageRecorder();
        var grant = await Store(recorder).ConferAdmissionGrantAsync(
            Tenant, Admitted, Admitter, Permissions, At, TestAdmissions.SignedBy(Admitter, Admitted));

        Assert.Equal(Adr0038, recorder.Stages);
        var persisted = await PersistedAsync();
        Assert.Equal(grant!.GrantId.ToString(), Assert.Single(persisted.Grants));
        Assert.Equal(Permissions.Count, persisted.Definitions);
        Assert.Equal(1, persisted.AdmissionRoles);
        var audit = Assert.Single(persisted.Audit);
        Assert.Equal(NodeEfAuthorizationConfigurationStore.AdmissionGrantConferredEventType.Value, audit.EventType);
        Assert.Contains(grant.GrantId.ToString(), audit.BodyJson, StringComparison.Ordinal);
        Assert.Equal(Admitter, audit.Actor);
        // The derived definition ids are the ones installs already hold: SHA-256(grant id ":" permission)[..16].
        await using var db = _store.CreateContext();
        Assert.Equal(
            Permissions.Permissions.Select(permission => new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(grant.GrantId.Value.ToString("D") + ":" + permission)).AsSpan(0, 16)).ToString()).Order(),
            (await db.AuthorizationDefinitions.Select(row => row.DefinitionId).ToArrayAsync()).Order());
    }

    // One fixture per stage that refuses there and nowhere else. Authorize, validate and commit refuse through
    // their real collaborators (the signed roster, definition admission, the grants table); bind, mutate and
    // react refuse through the cancellation check each stage makes on entry. React runs after commit has
    // saved, so its refusal proves the commit stage wrote inside the fence rather than around it.
    [Theory(DisplayName = "ck-10 conferral: a refusal at any stage stops there and persists nothing")]
    [InlineData(WritePipelineStage.Authorize)]
    [InlineData(WritePipelineStage.Bind)]
    [InlineData(WritePipelineStage.Mutate)]
    [InlineData(WritePipelineStage.Validate)]
    [InlineData(WritePipelineStage.Commit)]
    [InlineData(WritePipelineStage.React)]
    public async Task Conferral_EachStageRefusalStopsThereAndPersistsNothing(WritePipelineStage refusing)
    {
        using var cancel = new CancellationTokenSource();
        var recorder = new StageRecorder(
            refusing is WritePipelineStage.Bind or WritePipelineStage.Mutate or WritePipelineStage.React
                ? refusing : null, cancel);
        // Authorize: the roster records the party's admission by the founder, not by the claimed admitter.
        var authority = TestAdmissions.SignedBy(refusing == WritePipelineStage.Authorize ? "stranger" : Admitter, Admitted);
        var permissions = refusing == WritePipelineStage.Validate ? PermissionSet.From(["unknown:read"]) : Permissions;
        if (refusing == WritePipelineStage.Commit)
            await ExecuteAsync("CREATE TRIGGER ck10_commit_fault BEFORE INSERT ON search_grants BEGIN SELECT RAISE(ABORT, 'ck10'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => Store(recorder).ConferAdmissionGrantAsync(
            Tenant, Admitted, Admitter, permissions, At, authority, cancel.Token));

        Assert.Equal(Adr0038.TakeWhile(stage => stage != refusing).Append(refusing), recorder.Stages);
        await AssertNothingPersistedAsync();
    }

    [Fact(DisplayName = "ck-10 conferral: a crash between the grant and its audit leaves neither")]
    public async Task Conferral_CrashBetweenGrantAndAuditLeavesNeither()
    {
        await ExecuteAsync("CREATE TRIGGER ck10_audit_crash BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'ck10'); END;");
        var recorder = new StageRecorder();

        await Assert.ThrowsAnyAsync<Exception>(() => Store(recorder).ConferAdmissionGrantAsync(
            Tenant, Admitted, Admitter, Permissions, At, TestAdmissions.SignedBy(Admitter, Admitted)));

        Assert.Equal(WritePipelineStage.Commit, recorder.Stages[^1]);
        await AssertNothingPersistedAsync();
    }

    [Fact(DisplayName = "ck-10 narrowing: the reissue is refused at authorize under a decision over another grant, and nothing moves")]
    public async Task Narrowing_RefusedAtAuthorizeUnderAnotherGrantsDecisionLeavesTheOriginalLive()
    {
        var original = await ConferAsync();
        var recorder = new StageRecorder();
        var otherGrantsDecision = await DecisionAsync(new GrantId(Guid.NewGuid()));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store(recorder).NarrowAdmissionGrantAsync(
            Tenant, original.GrantId, PermissionSet.From([Permission.ContactsRead]), Revocation(),
            Guid.NewGuid(), otherGrantsDecision));

        Assert.Equal(Adr0038.Take(1), recorder.Stages);
        await AssertOnlyTheOriginalAsync(original);
    }

    [Fact(DisplayName = "ck-10 narrowing: a reissue attributed to a principal the decision did not admit is refused at authorize")]
    public async Task Narrowing_RefusedAtAuthorizeWhenAttributedToAnotherPrincipal()
    {
        var original = await ConferAsync();
        var recorder = new StageRecorder();
        var decision = await DecisionAsync(original.GrantId);
        var byAnother = new GrantRevocation(new ActorId("another-admin"), At,
            new GrantReason(GrantReasonCodes.RevocationReview, "ck10"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store(recorder).NarrowAdmissionGrantAsync(
            Tenant, original.GrantId, PermissionSet.From([Permission.ContactsRead]), byAnother, Guid.NewGuid(), decision));

        Assert.Equal(Adr0038.Take(1), recorder.Stages);
        await AssertOnlyTheOriginalAsync(original);
    }

    [Fact(DisplayName = "ck-10 narrowing: a crash at the reissue's audit rolls the revoke leg back with it")]
    public async Task Narrowing_CrashAtTheReissueAuditRollsBackTheRevokeLeg()
    {
        var original = await ConferAsync();
        await ExecuteAsync("CREATE TRIGGER ck10_audit_crash BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'ck10'); END;");
        var recorder = new StageRecorder();

        await Assert.ThrowsAnyAsync<Exception>(async () => await Store(recorder).NarrowAdmissionGrantAsync(
            Tenant, original.GrantId, PermissionSet.From([Permission.ContactsRead]), Revocation(),
            Guid.NewGuid(), await DecisionAsync(original.GrantId)));

        Assert.Equal(Adr0038.Take(5), recorder.Stages);
        await ExecuteAsync("DROP TRIGGER ck10_audit_crash;");
        await AssertOnlyTheOriginalAsync(original);
    }

    [Fact(DisplayName = "ck-10 narrowing: the reissue runs every stage and commits with the revoke leg and its authorized audit")]
    public async Task Narrowing_RunsEveryStageAndCommitsWithItsAuthorizedAudit()
    {
        var original = await ConferAsync();
        var recorder = new StageRecorder();
        var decision = await DecisionAsync(original.GrantId);

        var narrowing = await Store(recorder).NarrowAdmissionGrantAsync(
            Tenant, original.GrantId, PermissionSet.From([Permission.ContactsRead]), Revocation(),
            Guid.NewGuid(), decision);

        Assert.Equal(Adr0038, recorder.Stages);
        var persisted = await PersistedAsync();
        Assert.Equal(2, persisted.Grants.Length);
        Assert.Contains(narrowing!.Reissued.GrantId.ToString(), persisted.Grants);
        Assert.Contains(persisted.Audit, row => row.EventType == NodeEfAuthorizationConfigurationStore.AdmissionGrantConferredEventType.Value
            && row.AuthoritySnapshotJson is not null && row.BodyJson.Contains(narrowing.Reissued.GrantId.ToString(), StringComparison.Ordinal));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private NodeEfAuthorizationConfigurationStore Store(IWritePipelineObserver? observer = null) =>
        new(_store.Factory, new InMemoryRoleVocabulary([]), observer);

    private async Task<AccessGrant> ConferAsync() =>
        (await Store().ConferAdmissionGrantAsync(
            Tenant, Admitted, Admitter, Permissions, At, TestAdmissions.SignedBy(Admitter, Admitted)))!;

    private static GrantRevocation Revocation() =>
        new(new ActorId(Admitter), At, new GrantReason(GrantReasonCodes.RevocationReview, "ck10"));

    private static async Task<AuthorizationDecision> DecisionAsync(GrantId target) =>
        await TestAuthorization.AllowGate().DecideAsync(new AuthorizationWriteContext(new ActorId(Admitter), Tenant, At)
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", target.ToString()));

    private async Task AssertNothingPersistedAsync()
    {
        var persisted = await PersistedAsync();
        Assert.Empty(persisted.Grants);
        Assert.Equal(0, persisted.Definitions);
        Assert.Equal(0, persisted.AdmissionRoles);
        Assert.Empty(persisted.Audit);
    }

    private async Task AssertOnlyTheOriginalAsync(AccessGrant original)
    {
        var persisted = await PersistedAsync();
        Assert.Equal(original.GrantId.ToString(), Assert.Single(persisted.Grants));
        Assert.Equal(Permissions.Count, persisted.Definitions);
        Assert.Single(persisted.Audit);
        await using var db = _store.CreateContext();
        Assert.Null((await db.Grants.AsNoTracking().SingleAsync()).RevokedAtUnixMs);
    }

    private async Task<(string[] Grants, int Definitions, int AdmissionRoles, Data.Search.AuditOutboxRow[] Audit)> PersistedAsync()
    {
        await using var db = _store.CreateContext();
        return (
            await db.Grants.AsNoTracking().Select(row => row.GrantId).ToArrayAsync(),
            await db.AuthorizationDefinitions.CountAsync(),
            await db.AuthorizationRoles.CountAsync(row => row.RoleName.StartsWith(AccessGrantAuthorizationSeed.AdmissionRolePrefix)),
            await db.AuditOutbox.AsNoTracking().ToArrayAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = _store.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed class StageRecorder(WritePipelineStage? cancelAt = null, CancellationTokenSource? cancel = null)
        : IWritePipelineObserver
    {
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage)
        {
            Stages.Add(stage);
            if (stage == cancelAt) cancel!.Cancel();
        }
    }
}
