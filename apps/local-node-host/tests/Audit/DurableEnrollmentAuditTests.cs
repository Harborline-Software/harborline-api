using System.Reflection;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.Payloads;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// T-986 (owner ruling 2026-09-29): the signed enrollment compensating-control envelope commits in
/// <c>local-node.db</c> in the SAME transaction as the roster change it records; a fault staging it rolls the
/// change back; delivery to the trail is not durability; and every event the recorder can record is covered.
/// Faults are SQLite triggers on the real tables, or a signer that throws. A restart is a new harness over the
/// same encrypted file.
/// </summary>
public sealed class DurableEnrollmentAuditTests : IAsyncLifetime
{
    private static readonly Guid Team = Guid.Parse("98600000-0000-0000-0000-000000000001");
    private static readonly TenantId Tenant = new("tenant-t986");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-29T09:00:00Z");
    private static readonly Ed25519Verifier Verifier = new();

    private readonly Ed25519Signer _founder = new(KeyPair.Generate());
    private readonly Ed25519Signer _member = new(KeyPair.Generate());
    private readonly Ed25519Signer _node = new(KeyPair.Generate());
    private DurableAuditHarness _audit = null!;

    public async Task InitializeAsync() => _audit = await DurableAuditHarness.CreateAsync();

    public async Task DisposeAsync() => await _audit.DisposeAsync();

    [Fact(DisplayName = "T-986: an admission's signed audit commits with its roster record and is read after a restart")]
    public async Task Admission_CommitsItsSignedAudit_AndItIsReadAfterARestart()
    {
        var (projection, admission) = await ProjectionWithAdmissionAsync();
        await using (projection)
        {
            await projection.PublishLocalAsync(admission, CancellationToken.None, (write, ct) =>
                Recorder().Within(write).RecordMemberAdmittedAsync(
                    Tenant, Team.ToString("D"), "founder", "member", _member.IssuerId.ToBase64Url(),
                    ["contacts:read"], "invite", "corr-1", ct));
        }

        // Crash point: the process stops after the commit and before any delivery. Nothing in memory survives.
        await using var restarted = _audit.Reopen();
        Assert.True(await RosterRowExistsAsync(restarted, admission.RecordId));
        var record = Assert.Single(await restarted.DeliveredAsync(Tenant, AuditEventType.MemberAdmitted));
        Assert.True(Verifier.Verify(record.Payload));
        Assert.Equal(_node.IssuerId, record.Payload.IssuerId);
        var body = record.Payload.Payload.Body;
        Assert.Equal("member", body["admitted_party_id"]);
        Assert.Equal("invite", body["admission_mode"]);
        Assert.Equal(["contacts:read"], Assert.IsAssignableFrom<IReadOnlyList<string>>(body["granted_permissions"]));
        // An ordinary security audit: no request decision authorized it, so it stores no authority snapshot.
        Assert.Null(record.AuthoritySnapshot);
        // Tamper-evident as stored: an edited body no longer verifies.
        Assert.False(Verifier.Verify(record.Payload with
        {
            Payload = new AuditPayload(new Dictionary<string, object?>(body) { ["admitted_party_id"] = "attacker" }),
        }));
    }

    [Fact(DisplayName = "T-986: a fault appending the audit entry rolls the admission's roster record back")]
    public async Task AppendFault_RollsTheRosterRecordBack()
    {
        var (projection, admission) = await ProjectionWithAdmissionAsync();
        await _audit.ExecuteAsync(
            "CREATE TRIGGER t986_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't986'); END;");

        await using (projection)
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => projection.PublishLocalAsync(
                admission, CancellationToken.None, (write, ct) =>
                    Recorder().Within(write).RecordOwnershipTransferredAsync(Tenant, Team.ToString("D"), "founder", "member", ct: ct)));
        }

        Assert.False(await RosterRowExistsAsync(_audit, admission.RecordId));
        await using var db = _audit.Store.CreateContext();
        Assert.Empty(await db.AuditOutbox.Where(row => row.TenantId == Tenant.Value).ToListAsync());
    }

    [Fact(DisplayName = "T-986: a fault signing the audit envelope refuses the admission instead of committing it unaudited")]
    public async Task SigningFault_RefusesTheRosterWrite()
    {
        var (projection, admission) = await ProjectionWithAdmissionAsync();
        var faulting = new KernelAuditEnrollmentCompensatingControlRecorder(new ThrowingSigner(), TimeProvider.System);

        await using (projection)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => projection.PublishLocalAsync(
                admission, CancellationToken.None, (write, ct) =>
                    faulting.Within(write).RecordMemberRevokedAsync(Tenant, Team.ToString("D"), "founder", "member", ct: ct)));
        }

        Assert.False(await RosterRowExistsAsync(_audit, admission.RecordId));
    }

    [Fact(DisplayName = "T-986: the recorder refuses to record outside the change it records")]
    public async Task UnboundRecorder_Refuses()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder()
            .RecordOwnershipTransferredAsync(Tenant, Team.ToString("D"), "founder", "member").AsTask());
        Assert.Contains(AuditEventType.OwnershipTransferred.Value, error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "T-986 inventory: every event the enrollment recorder records commits with the roster write and survives a restart")]
    public async Task EveryRecorderEvent_CommitsWithTheRosterWrite_AndSurvivesARestart()
    {
        var methods = typeof(IEnrollmentCompensatingControlRecorder).GetMethods()
            .Where(method => method.Name.StartsWith("Record", StringComparison.Ordinal))
            .ToArray();
        // One recorder method per payload the kernel defines for this cohort; a new event type fails here until
        // it is recorded, and so enters this test's coverage. AuditExported is the one payload that records no
        // enrollment change: AuditExportService appends it to the (now durable) trail when an export runs.
        var payloads = typeof(EnrollmentCompensatingControlPayloads).GetNestedTypes()
            .Where(type => type.Name.EndsWith("Payload", StringComparison.Ordinal))
            .Where(type => type != typeof(EnrollmentCompensatingControlPayloads.AuditExportedPayload))
            .ToArray();
        Assert.Equal(payloads.Length, methods.Length);
        Assert.Equal(
            payloads.Select(type => type.Name[..^"Payload".Length]).Order(StringComparer.Ordinal),
            methods.Select(method => method.Name["Record".Length..^"Async".Length]).Order(StringComparer.Ordinal));

        var recorder = Recorder();
        var roster = Roster();
        var admissions = roster.EnumerateAdmissions().ToArray();
        for (var i = 0; i < methods.Length; i++)
        {
            await using var write = _audit.RosterFactory.CreateDbContext();
            write.RosterRecords.Add(NodeRosterRecord.FromCrdtState(
                RosterRecordCrdtState.FromAdmission(admissions[i % admissions.Length]) with { RecordId = $"t986-{i}" }));
            await (ValueTask)methods[i].Invoke(recorder.Within(write), Arguments(methods[i]))!;
            await write.SaveChangesAsync();
        }

        await using var restarted = _audit.Reopen();
        await restarted.Outbox.DrainAsync();
        var delivered = new List<AuditRecord>();
        await foreach (var record in restarted.Trail.QueryAsync(new AuditQuery(Tenant)))
            delivered.Add(record);

        Assert.Equal(
            new[]
            {
                AuditEventType.MemberAdmitted.Value, AuditEventType.MemberRevoked.Value,
                AuditEventType.OwnershipTransferred.Value, AuditEventType.PermissionsGranted.Value,
            },
            delivered.Select(record => record.EventType.Value).Order(StringComparer.Ordinal));
        Assert.All(delivered, record => Assert.True(Verifier.Verify(record.Payload)));
        for (var i = 0; i < methods.Length; i++)
            Assert.True(await RosterRowExistsAsync(restarted, $"t986-{i}"));
    }

    [Fact(DisplayName = "T-986 crash point: a crash after the trail append and before the mark delivers no second entry after a restart")]
    public async Task CrashBetweenAppendAndMark_IsNotDeliveredTwiceAfterRestart()
    {
        var (projection, admission) = await ProjectionWithAdmissionAsync();
        await using (projection)
        {
            await projection.PublishLocalAsync(admission, CancellationToken.None, (write, ct) =>
                Recorder().Within(write).RecordPermissionsGrantedAsync(
                    Tenant, Team.ToString("D"), "founder", "member", ["ledger:post"], ct: ct));
        }

        await _audit.ExecuteAsync(
            "CREATE TRIGGER t986_mark BEFORE UPDATE ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't986'); END;");
        Assert.Equal(0, await _audit.Outbox.DrainAsync());
        await _audit.ExecuteAsync("DROP TRIGGER t986_mark;");

        await using var restarted = _audit.Reopen();
        Assert.Single(await restarted.DeliveredAsync(Tenant, AuditEventType.PermissionsGranted));
        await using var db = restarted.Store.CreateContext();
        Assert.All(await db.AuditOutbox.ToListAsync(), row => Assert.NotNull(row.PublishedAtUnixMs));
    }

    [Fact(DisplayName = "T-986: the host composition registers no in-memory audit trail")]
    public void Composition_RegistersNoInMemoryTrail()
    {
        var services = new ServiceCollection().AddEnrollmentCompensatingControlAudit();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(InMemoryAuditTrail)
            || descriptor.ImplementationType == typeof(InMemoryAuditTrail)
            || descriptor.ServiceType == typeof(InMemoryAuditEventReader));
    }

    private KernelAuditEnrollmentCompensatingControlRecorder Recorder() => new(_node, TimeProvider.System);

    private MemberRoster Roster() =>
        MemberRoster.Genesis(Team, "founder", _founder, Verifier, At, Guid.NewGuid())
            .Admit("founder", _founder, "member", _member.IssuerId, PermissionCompositions.Member,
                Verifier, At.AddSeconds(1), Guid.NewGuid());

    private async Task<(RosterCrdtProjection Projection, RosterRecordCrdtState Admission)> ProjectionWithAdmissionAsync()
    {
        var roster = Roster();
        var projection = new RosterCrdtProjection(
            TimeProvider.System, new YDotNetCrdtEngine(), _audit.RosterFactory, Verifier, _founder,
            NullLogger<RosterCrdtProjection>.Instance, new NodeTeamRoster(roster));
        var genesis = roster.EnumerateAdmissions().Single(a => a.Admission.IsGenesis);
        await projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(genesis), CancellationToken.None);
        var member = roster.EnumerateAdmissions().Single(a => !a.Admission.IsGenesis);
        return (projection, RosterRecordCrdtState.FromAdmission(member));
    }

    private static async Task<bool> RosterRowExistsAsync(DurableAuditHarness audit, string recordId)
    {
        await using var db = audit.RosterFactory.CreateDbContext();
        return await db.RosterRecords.AnyAsync(row => row.Id == recordId);
    }

    private static object?[] Arguments(MethodInfo method) => method.GetParameters().Select(parameter =>
        parameter.ParameterType == typeof(TenantId) ? Tenant
        : parameter.ParameterType == typeof(IReadOnlyList<string>) ? new[] { "ledger:post" }
        : parameter.ParameterType == typeof(CancellationToken) ? CancellationToken.None
        : (object?)$"{parameter.Name}-t986").ToArray();

    private sealed class ThrowingSigner : IOperationSigner
    {
        public PrincipalId IssuerId => throw new InvalidOperationException("signer unavailable");

        public ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default) =>
            throw new InvalidOperationException("signer unavailable");
    }
}
