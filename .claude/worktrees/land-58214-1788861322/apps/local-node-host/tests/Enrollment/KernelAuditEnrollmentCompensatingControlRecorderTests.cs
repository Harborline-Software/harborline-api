using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Enrollment;

/// <summary>
/// Enrollment Phase C — the host adapter that wires the enrollment <c>IEnrollmentCompensatingControlRecorder</c> seam to the unified
/// kernel audit trail (compensating control #1, the "second set of eyes"; <c>project_sod_compensating_controls</c>).
/// Proves the SoD-significant enrollment operations land in the SAME append-only, signed, tamper-evident,
/// undeletable trail the financial posts use, readable via the <c>audit:read</c>-gated read-side, and that a
/// tampered record fails verification.
/// </summary>
public sealed class KernelAuditEnrollmentCompensatingControlRecorderTests
{
    [Fact]
    public async Task ShippingComposition_OrdinarySecurityAuditStoresNullAuthoritySnapshot()
    {
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var nodeSigner = new NodePrincipalSigner(seed);
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(nodeSigner)
            .AddSingleton<IOperationSigner>(nodeSigner.Signer)
            .AddEnrollmentCompensatingControlAudit()
            .BuildServiceProvider();
        using var authority = NodeCallerAttributionScope.Enter(new NodeCallerAttribution(
            MemberPartyId: "approver-party",
            MembershipId: "membership-1",
            MembershipOwnerVersion: 8,
            SessionCorrelationId: "session-1",
            CoordinationCorrelationId: "coordination-1",
            AuthorizationEpoch: 12));
        var tenant = new TenantId("tenant-live-195");

        await provider.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.IEnrollmentCompensatingControlRecorder>()
            .RecordPermissionsGrantedAsync(
                tenant, "team-1", "approver-party", "member-2", ["gl:post"]);

        var page = await provider.GetRequiredService<IAuditEventReader>().ListAsync(
            tenant,
            new AuditEventReaderQuery(EventType: AuditEventType.PermissionsGranted));
        Assert.Null(Assert.Single(page.Records).AuthoritySnapshot);
    }

    private static (KernelAuditEnrollmentCompensatingControlRecorder sink, IAuditTrail trail, IAuditEventReader reader, IServiceScope scope)
        BuildHarness()
    {
        var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var sp = new ServiceCollection()
            .AddSingleton<IOperationSigner>(signer)
            .AddSingleton<IOperationVerifier, Ed25519Verifier>()
            .AddHarborlineKernelAuditReaderInMemory()
            .BuildServiceProvider();
        var scope = sp.CreateScope();
        var trail = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
        var reader = scope.ServiceProvider.GetRequiredService<IAuditEventReader>();
        var sink = new KernelAuditEnrollmentCompensatingControlRecorder(trail, signer, time: TimeProvider.System);
        return (sink, trail, reader, scope);
    }

    [Fact]
    public async Task RecordsEverySeparationSignificantEnrollmentOp_IntoTheUnifiedAuditTrail()
    {
        var (sink, _, reader, scope) = BuildHarness();
        using var _s = scope;
        var tenant = new TenantId("tenant-a");

        await sink.RecordMemberAdmittedAsync(
            tenant, "team-1", "admin-1", "joiner-1", "joiner-pubkey-b64",
            new[] { "contacts:read", "gl:read" }, "invite", correlationId: "corr-1");
        await sink.RecordMemberRevokedAsync(tenant, "team-1", "admin-1", "joiner-1");
        await sink.RecordPermissionsGrantedAsync(
            tenant, "team-1", "owner-1", "member-1", new[] { "gl:post" });
        await sink.RecordOwnershipTransferredAsync(tenant, "team-1", "owner-1", "owner-2");

        // All four SoD-significant op classes are queryable in the one trail (the "second set of eyes").
        Assert.Equal(1, await CountAsync(reader, tenant, AuditEventType.MemberAdmitted));
        Assert.Equal(1, await CountAsync(reader, tenant, AuditEventType.MemberRevoked));
        Assert.Equal(1, await CountAsync(reader, tenant, AuditEventType.PermissionsGranted));
        Assert.Equal(1, await CountAsync(reader, tenant, AuditEventType.OwnershipTransferred));

        // Payload carries the SoD-relevant facts a reviewer needs.
        var admitted = (await reader.ListAsync(tenant,
            new AuditEventReaderQuery(EventType: AuditEventType.MemberAdmitted))).Records[0];
        var body = admitted.Payload.Payload.Body;
        Assert.Equal("admin-1", body["admitter_party_id"]);
        Assert.Equal("joiner-1", body["admitted_party_id"]);
        Assert.Equal("invite", body["admission_mode"]);
    }

    [Fact]
    public async Task RecordedAuditEnvelope_IsSigned_AndTamperEvident()
    {
        var (sink, _, reader, scope) = BuildHarness();
        using var _s = scope;
        var tenant = new TenantId("tenant-a");
        await sink.RecordOwnershipTransferredAsync(tenant, "team-1", "owner-1", "owner-2");

        var rec = (await reader.ListAsync(tenant,
            new AuditEventReaderQuery(EventType: AuditEventType.OwnershipTransferred))).Records[0];

        var verifier = new Ed25519Verifier();
        // The stored envelope verifies as-signed (tamper-evident — the signature covers the canonical bytes).
        Assert.True(verifier.Verify(rec.Payload));
        // A tampered variant (mutated body) FAILS verification — a silent edit is detectable.
        var tampered = rec.Payload with
        {
            Payload = new AuditPayload(new Dictionary<string, object?> { ["from_party_id"] = "attacker" }),
        };
        Assert.False(verifier.Verify(tampered));
    }

    [Fact]
    public async Task FaultInTrail_IsSwallowed_DoesNotBrickTheEnrollmentOp_ButSignalsLoud()
    {
        // Fail-safe-but-LOUD (#1295 F2): a faulting trail must NOT throw back into the admit/revoke path, but it
        // must NOT be silent either — onFault fires, carrying the event type so the host can escalate.
        var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        AuditEventType? capturedType = null;
        Exception? captured = null;
        var sink = new KernelAuditEnrollmentCompensatingControlRecorder(
            new ThrowingAuditTrail(), signer,
            onFault: (eventType, ex) => { capturedType = eventType; captured = ex; }, time: TimeProvider.System);

        // Must complete without throwing despite the trail blowing up.
        await sink.RecordMemberRevokedAsync(new TenantId("tenant-a"), "team-1", "admin-1", "joiner-1");
        Assert.NotNull(captured);
        Assert.Equal(AuditEventType.MemberRevoked, capturedType);
    }

    [Fact]
    public async Task OwnershipTransferFault_SignalsLoud_WithTheHighStakesEventType()
    {
        // The highest-stakes op (OwnershipTransferred — the root-grant moving) must reach onFault with its event
        // type so the host can escalate it above a plain warning. Fail-safe still holds (no throw).
        var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        AuditEventType? capturedType = null;
        var sink = new KernelAuditEnrollmentCompensatingControlRecorder(
            new ThrowingAuditTrail(), signer, onFault: (eventType, _) => capturedType = eventType, time: TimeProvider.System);

        await sink.RecordOwnershipTransferredAsync(new TenantId("tenant-a"), "team-1", "owner-1", "owner-2");
        Assert.Equal(AuditEventType.OwnershipTransferred, capturedType);
    }

    private static async Task<int> CountAsync(IAuditEventReader reader, TenantId tenant, AuditEventType type)
    {
        var page = await reader.ListAsync(tenant, new AuditEventReaderQuery(EventType: type));
        return page.Records.Count;
    }

    private sealed class ThrowingAuditTrail : IAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("trail unavailable");
        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
