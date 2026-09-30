using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

/// <summary>
/// T-1000 (DES-0029 kernel-core-ck-6, ck-11): a compromised-device revocation stages its signed
/// <see cref="AuditEventType.MemberRevoked"/> entry through the bound enrollment recorder in the SAME save as the
/// roster revocation record, so both commit or neither does. Faults are a SQLite trigger on the real outbox table
/// or a signer that throws; a restart is a new harness over the same encrypted <c>local-node.db</c>.
/// </summary>
public sealed class RevocationEnrollmentAuditTests : IAsyncLifetime
{
    private const string Revoked = "stolen-node";
    private const string CorrelationId = "4ee2aa00-0000-0000-0000-000000001000";
    private static readonly Guid Team = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-30T09:00:00Z");
    private static readonly Ed25519Verifier Verifier = new();

    private readonly Ed25519Signer _founder = new(KeyPair.Generate());
    private readonly Ed25519Signer _node = new(KeyPair.Generate());
    private DurableAuditHarness _audit = null!;

    public async Task InitializeAsync() => _audit = await DurableAuditHarness.CreateAsync();

    public async Task DisposeAsync() => await _audit.DisposeAsync();

    [Fact(DisplayName = "T-1000: a device-lost revocation stages its MemberRevoked audit in the roster save, and it is read after a restart")]
    public async Task Revocation_StagesItsAuditInTheRosterSave_AndItIsReadAfterARestart()
    {
        var decision = Decision();
        CompromisedDeviceRevocation evidence;
        await using (var projection = Projection(out var live))
        {
            evidence = await Publisher(projection, live, Recorder()).RevokeAsync(Request(), CorrelationId, decision);
            Assert.False(live.Current.Contains(Revoked));
        }

        // Crash point: the process stops after the commit and before any delivery. The entry is already durable
        // beside the record, in the same file, because the one save wrote both.
        await using var restarted = _audit.Reopen();
        Assert.True(await RevocationRowExistsAsync(restarted, evidence.RecordId));
        await using (var db = restarted.Store.CreateContext())
        {
            var staged = Assert.Single(await db.AuditOutbox
                .Where(row => row.EventType == AuditEventType.MemberRevoked.Value).ToListAsync());
            Assert.Null(staged.PublishedAtUnixMs);
        }

        var record = Assert.Single(await restarted.DeliveredAsync(decision.Request.Tenant, AuditEventType.MemberRevoked));
        Assert.True(Verifier.Verify(record.Payload));
        Assert.Equal(decision.Request.Principal, record.Actor);
        Assert.Equal(decision.Request.Target, record.Target);
        Assert.Equal(decision.DecidedAt, record.OccurredAt);
        Assert.NotNull(record.AuthoritySnapshot);
        var body = record.Payload.Payload.Body;
        Assert.Equal(Revoked, body["revoked_party_id"]);
        Assert.Equal("operator-a", body["revoker_party_id"]);
        Assert.Equal(CorrelationId, body["correlation_id"]);
        Assert.Equal(MemberRevocationReasons.DeviceLost, body["reason"]);
    }

    [Fact(DisplayName = "T-1000: a fault appending the revocation's audit refuses the revocation and writes no roster record")]
    public async Task AppendFault_RefusesTheRevocation_AndWritesNoRosterRecord()
    {
        await _audit.ExecuteAsync(
            "CREATE TRIGGER t1000_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't1000'); END;");

        await using (var projection = Projection(out var live))
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
                await Publisher(projection, live, Recorder()).RevokeAsync(Request(), CorrelationId, Decision()));
            Assert.True(live.Current.Contains(Revoked));
            Assert.DoesNotContain(projection.Snapshot(), record => record.Kind == RosterRecordKind.Revocation);
        }

        await AssertNothingCommittedAsync();
    }

    [Fact(DisplayName = "T-1000: a fault signing the revocation's audit refuses the revocation and writes no roster record")]
    public async Task SigningFault_RefusesTheRevocation_AndWritesNoRosterRecord()
    {
        var faulting = new KernelAuditEnrollmentCompensatingControlRecorder(new ThrowingSigner(), TimeProvider.System);

        await using (var projection = Projection(out var live))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Publisher(projection, live, faulting).RevokeAsync(Request(), CorrelationId, Decision()));
            Assert.True(live.Current.Contains(Revoked));
        }

        await AssertNothingCommittedAsync();
    }

    private async Task AssertNothingCommittedAsync()
    {
        await using var restarted = _audit.Reopen();
        await using (var roster = restarted.RosterFactory.CreateDbContext())
        {
            Assert.False(await roster.RosterRecords.AnyAsync(row => row.Kind == (int)RosterRecordKind.Revocation));
        }

        await using var db = restarted.Store.CreateContext();
        Assert.Empty(await db.AuditOutbox.ToListAsync());
    }

    private NodeRosterCompromisedDeviceRevocationPublisher Publisher(
        RosterCrdtProjection projection, NodeTeamRoster live, IEnrollmentCompensatingControlRecorder recorder) =>
        new(new NodeRosterMemberRevocationAuthority(
            live,
            new RosterRevocationProjection(projection),
            _founder,
            Verifier,
            _audit.Trail,
            new NodeAdministratorAuthority(_audit.RosterFactory, TimeProvider.System, TestAuthorization.AllowGate()),
            recorder));

    private KernelAuditEnrollmentCompensatingControlRecorder Recorder() => new(_node, TimeProvider.System);

    private RosterCrdtProjection Projection(out NodeTeamRoster live)
    {
        var roster = MemberRoster.Genesis(Team, "operator-a", _founder, Verifier, At, Guid.NewGuid())
            .Admit("operator-a", _founder, Revoked, KeyPair.Generate().PrincipalId, PermissionCompositions.Member,
                Verifier, At.AddMinutes(1), Guid.NewGuid());
        live = new NodeTeamRoster(roster);
        return new RosterCrdtProjection(
            TimeProvider.System, new YDotNetCrdtEngine(), _audit.RosterFactory, Verifier, _founder,
            NullLogger<RosterCrdtProjection>.Instance, live);
    }

    private static CompromisedDeviceResponseRequest Request() => new(Team.ToString("D"), Revoked, "operator-a");

    private Harborline.Api.Foundation.Authorization.AuthorizationDecision Decision() =>
        TestAuthorization.AllowedDecision(
            new TenantId(Team.ToString("D")), Revoked, "members", TeamRolePermissions.MembersManage,
            _founder.IssuerId.ToBase64Url(), At.AddMinutes(2));

    private static async Task<bool> RevocationRowExistsAsync(DurableAuditHarness audit, string recordId)
    {
        await using var db = audit.RosterFactory.CreateDbContext();
        return await db.RosterRecords.AnyAsync(row => row.Id == recordId && row.Kind == (int)RosterRecordKind.Revocation);
    }

    private sealed class ThrowingSigner : IOperationSigner
    {
        public PrincipalId IssuerId => throw new InvalidOperationException("signer unavailable");

        public ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default) =>
            throw new InvalidOperationException("signer unavailable");
    }
}
