using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterPreInsertVerificationTests
{
    private static readonly Guid Tenant = Guid.Parse("29510000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(10);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSignatureNeverReachesDurableStore(bool revoke)
    {
        await using var f = await Fixture.CreateAsync();
        var record = revoke ? f.Revocation(f.Founder, "founder", "member")
            : f.Admission(f.Founder, "founder", "new");
        record = record with { SignatureB64Url = new string('A', 86) };
        await f.MergeAsync([record]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == record.RecordId);
        var rows = await f.AuditsAsync();
        var row = Assert.Single(rows);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(row.Payload.Payload.Body));
        Assert.Equal("roster.record.signature_invalid", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
        Assert.Contains(record.RecordId, body.RootElement.GetProperty("diagnostic").GetString());
        Assert.True(Verifier.Verify(row.Payload));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Projection.ReconcileAsync(default)));
        await f.MergeAsync([record]);
        Assert.Single(await f.AuditsAsync());
    }

    [Fact]
    public async Task RestartUsesDurableChainAndRefusedRecordsAreAbsentFromHydration()
    {
        await using var f = await Fixture.CreateAsync();
        var valid = f.Admission(f.Founder, "founder", "valid");
        var bad = f.Admission(f.Founder, "founder", "bad") with { SignatureB64Url = new string('A', 86) };
        await f.MergeAsync([bad, valid]);
        await f.RestartAsync();
        // No hydration or live membership seed: the next inbound check must read durable evidence.
        var afterRestart = f.Admission(f.Founder, "founder", "after-restart");
        await f.MergeAsync([afterRestart]);
        var stored = await f.StoredAsync();
        Assert.Contains(stored, r => r.RecordId == valid.RecordId);
        Assert.Contains(stored, r => r.RecordId == afterRestart.RecordId);
        Assert.DoesNotContain(stored, r => r.RecordId == bad.RecordId);
        await f.Projection.HydrateFromStoreAsync(default);
        await f.Projection.DrainPendingReconcilesAsync();
        Assert.DoesNotContain(f.Projection.Snapshot(), r => r.RecordId == bad.RecordId);
        var reader = f.Provider.GetRequiredService<IVerifiedTenantRosterReader>();
        var rebuilt = await reader.ReadAsync(new TenantId(Tenant.ToString("D")), default);
        Assert.True(rebuilt.Contains("valid"));
        Assert.True(rebuilt.Contains("after-restart"));
        Assert.False(rebuilt.Contains("bad"));
    }

    [Fact]
    public async Task DurableGenesisRefusesBackdatedSecondRoot()
    {
        await using var f = await Fixture.CreateAsync();
        var root = MemberRoster.Genesis(Tenant, "intruder", f.Member, Verifier, At.AddDays(-1), Guid.NewGuid());
        var candidate = RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single());
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        Assert.Contains("roster.genesis.duplicate", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedSignerCannotAddRecordsLaterInTheChain(bool revoke)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        await f.MergeAsync([f.Revocation(f.Founder, "founder", "member")]);
        var candidate = revoke ? f.Revocation(f.Member, "member", "founder", At.AddHours(2))
            : f.Admission(f.Member, "member", "late", At.AddHours(2));
        await f.RestartAsync();
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.chain_ineligible", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task ReverseOrderedValidChainIsStoredAndBadRecordAuditIsAwaited()
    {
        var trail = new DelayedTrail();
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner, trail);
        var child = f.Admission(f.Member, "member", "child");
        await f.MergeAsync([child]);
        Assert.Contains(await f.StoredAsync(), r => r.RecordId == child.RecordId);
        var bad = f.Admission(f.Founder, "founder", "bad") with { SignatureB64Url = new string('A', 86) };
        var merge = f.MergeAsync([bad]);
        try
        {
            Assert.Same(trail.Started.Task, await Task.WhenAny(trail.Started.Task, merge));
            Assert.False(merge.IsCompleted);
            Assert.Empty(await f.AuditsAsync());
        }
        finally { trail.Release.TrySetResult(); }
        await merge;
        Assert.Single(await f.AuditsAsync());
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == bad.RecordId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveOrderSurvivesRestartAndLateBackdatingCannotReplaceEarlierRemoval(bool earlierLegitimate)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var first = earlierLegitimate
            ? f.Revocation(f.Member, "member", "founder", At.AddMinutes(2))
            : f.Revocation(f.Founder, "founder", "member", At.AddHours(1));
        first = first.AttestReceipt(f.Founder, "founder", At.AddHours(2));
        await f.MergeRawAsync([first]);
        DateTimeOffset? receipt;
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var rows = await db.RosterRecords.AsNoTracking().ToListAsync();
            Assert.All(rows, row => Assert.NotNull(row.ReceivedAtUtc));
            Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.ReceiveAttestationSignatureB64Url)));
            receipt = rows.Single(row => row.Id == first.RecordId).ReceivedAtUtc;
        }
        await f.RestartAsync();
        await f.MergeRawAsync([first]);
        await using (var db = await f.Factory.CreateDbContextAsync())
            Assert.Equal(receipt, (await db.RosterRecords.SingleAsync(row => row.Id == first.RecordId)).ReceivedAtUtc);
        var late = earlierLegitimate
            ? f.Revocation(f.Founder, "founder", "member", At.AddMinutes(1))
            : f.Revocation(f.Member, "member", "founder", At.AddMinutes(2));
        late = late.AttestReceipt(f.Founder, "founder", At.AddHours(2).AddSeconds(1));
        await f.MergeRawAsync([late]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == late.RecordId);
        var reader = f.Provider.GetRequiredService<IVerifiedTenantRosterReader>();
        var rebuilt = await reader.ReadAsync(new TenantId(Tenant.ToString("D")), default);
        Assert.Equal(earlierLegitimate, rebuilt.Contains("member"));
        Assert.Equal(!earlierLegitimate, rebuilt.Contains("founder"));
        Assert.Contains("roster.record.chain_ineligible", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-record")]
    [InlineData("altered-time")]
    public async Task ForgedReceiveAttestationIsRefusedBeforeDurableInsert(string mutation)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", $"candidate-{mutation}", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2));
        candidate = mutation switch
        {
            "wrong-key" => candidate with { ReceivedByPublicKey = f.Member.IssuerId.ToBase64Url() },
            "wrong-record" => CopyReceipt(candidate,
                f.Admission(f.Founder, "founder", "other-record", At.AddHours(2))
                    .AttestReceipt(f.Founder, "founder", At.AddHours(2))),
            _ => candidate with { ReceivedAtIso = At.AddHours(3).ToString("O") },
        };
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.receive_attestation_invalid",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task FutureOrderTimeIsRefusedAndReceiptWindowEdgeIsAccepted()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var received = At.AddHours(2);
        var forgedFuture = f.Admission(f.Founder, "founder", "forged-future",
                received + NodeRosterRecord.ReceiveTimeWindow + TimeSpan.FromMilliseconds(1))
            .AttestReceipt(f.Founder, "founder", received);
        var atWindowEdge = f.Admission(f.Founder, "founder", "window-edge",
                received + NodeRosterRecord.ReceiveTimeWindow)
            .AttestReceipt(f.Founder, "founder", received);

        await f.MergeRawAsync([forgedFuture, atWindowEdge]);

        var stored = await f.StoredAsync();
        Assert.DoesNotContain(stored, row => row.RecordId == forgedFuture.RecordId);
        Assert.Contains(stored, row => row.RecordId == atWindowEdge.RecordId);
        Assert.Contains("roster.record.order_time_future",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task OldWireShapeIsRefusedAtTheVersionBoundary()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "old-peer", At.AddHours(2)) with
        {
            WireFormatVersion = 3,
            UnmappedWireFields = PermissionField(),
        };
        candidate = JsonSerializer.Deserialize<RosterRecordCrdtState>(JsonSerializer.Serialize(candidate))!;
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster_wire_format_unsupported",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task CurrentWireShapeWithPermissionFieldIsRefusedAsMalformed()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "stray-permissions", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2)) with
        {
            UnmappedWireFields = PermissionField(),
        };
        var json = JsonSerializer.Serialize(candidate);
        Assert.Contains("\"Permissions\"", json, StringComparison.Ordinal);
        candidate = JsonSerializer.Deserialize<RosterRecordCrdtState>(json)!;
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.malformed",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    // DES-0029 kernel-core-ck-11: each test below kills a surviving mutant in VerifyInboundRecord whose outcome is
    // a durable insert of evidence the chain must refuse (docs/evidence/ck11-roster-mutation-2026-09-29.md).
    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_member_holding_admit_but_not_revoke_cannot_store_a_revocation()
    {
        await using var f = await Fixture.CreateAsync(PermissionSet.Of(Permission.MembersAdmit));
        await f.MergeAsync([f.Admission(f.Founder, "founder", "third", At.AddMinutes(30))]);
        var candidate = f.Revocation(f.Member, "member", "third", At.AddHours(2));
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_revocation_signed_by_one_key_in_another_parties_name_never_reaches_the_durable_store()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        // The member's key signs a revocation that names the founder as its revoker.
        var candidate = f.Revocation(f.Member, "founder", "member", At.AddHours(2));
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_genesis_whose_receipt_no_member_of_its_chain_attested_never_reaches_the_durable_store()
    {
        await using var f = await Fixture.CreateAsync();
        var team = Guid.Parse("29510000-0000-0000-0000-0000000000c1");
        var root = MemberRoster.Genesis(team, "intruder", f.Member, Verifier, At, Guid.NewGuid());
        var candidate = RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single())
            .AttestReceipt(f.Founder, "founder", At);
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_receipt_signed_by_one_key_in_the_founders_name_does_not_admit_the_record()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "forged-receipt", At.AddHours(2))
            .AttestReceipt(f.Member, "founder", At.AddHours(2));
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.receive_attestation_untrusted");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_revocation_in_the_same_delivery_refuses_the_revoked_members_later_admission()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var revocation = f.Revocation(f.Founder, "founder", "member", At.AddHours(1))
            .AttestReceipt(f.Founder, "founder", At.AddHours(1));
        var late = f.Admission(f.Member, "member", "late", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2));
        await f.MergeRawAsync([revocation, late]);
        var stored = await f.StoredAsync();
        Assert.Contains(stored, r => r.RecordId == revocation.RecordId);
        Assert.DoesNotContain(stored, r => r.RecordId == late.RecordId);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_same_instant_revocation_with_the_lower_nonce_refuses_the_revoked_members_admission()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var instant = At.AddHours(1);
        var revocation = RosterRecordCrdtState.FromRevocation(new MemberRevocationRecord(Tenant.ToString("D"), "member",
                RosterSigning.SignRevocation(f.Founder, Tenant, "member", "founder", instant,
                    Guid.Parse("00000000-0000-0000-0000-000000000001"))))
            .AttestReceipt(f.Founder, "founder", instant);
        await f.MergeRawAsync([revocation]);
        Assert.Contains(await f.StoredAsync(), r => r.RecordId == revocation.RecordId);
        var key = KeyPair.Generate().PrincipalId;
        var late = RosterRecordCrdtState.FromAdmission(new MemberAdmissionRecord(Tenant.ToString("D"), "late", key,
                RosterSigning.SignAdmission(f.Member, Tenant, "late", key, "member", false, instant,
                    Guid.Parse("ffffffff-0000-0000-0000-000000000000"))))
            .AttestReceipt(f.Founder, "founder", instant);
        await f.MergeRawAsync([late]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == late.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_self_attested_second_root_arriving_after_an_ordinary_admission_is_still_a_duplicate()
    {
        await using var f = await Fixture.CreateAsync();
        var ordinary = f.Admission(f.Founder, "founder", "ordinary", At.AddHours(1))
            .AttestReceipt(f.Founder, "founder", At.AddHours(1));
        var root = MemberRoster.Genesis(Tenant, "intruder", f.Member, Verifier, At.AddHours(2), Guid.NewGuid());
        var intruder = RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single())
            .AttestReceipt(f.Member, "intruder", At.AddHours(2));
        await f.MergeRawAsync([ordinary, intruder]);
        var stored = await f.StoredAsync();
        Assert.Contains(stored, r => r.RecordId == ordinary.RecordId);
        Assert.DoesNotContain(stored, r => r.RecordId == intruder.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.genesis.duplicate");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_second_admission_for_a_party_already_in_the_chain_never_reaches_the_durable_store()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "member", At.AddHours(2));
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_member_holding_revoke_but_not_admit_cannot_store_an_admission()
    {
        await using var f = await Fixture.CreateAsync(PermissionSet.Of(Permission.MembersRevoke));
        var candidate = f.Admission(f.Member, "member", "late", At.AddHours(2));
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_same_instant_same_nonce_revocation_whose_signature_sorts_first_refuses_the_revoked_members_admission()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var instant = At.AddHours(1);
        var nonce = Guid.Parse("7f000000-0000-0000-0000-000000000000");
        var revocation = RosterRecordCrdtState.FromRevocation(new MemberRevocationRecord(Tenant.ToString("D"), "member",
                RosterSigning.SignRevocation(f.Founder, Tenant, "member", "founder", instant, nonce)))
            .AttestReceipt(f.Founder, "founder", instant);
        // Ed25519 is deterministic, so pick the admitted party until the tie-break (ordinal signature order) puts
        // the revocation first: that revocation precedes the admission and ejects its signer.
        var late = Enumerable.Range(0, 64).Select(i =>
            {
                var key = KeyPair.Generate().PrincipalId;
                return RosterRecordCrdtState.FromAdmission(new MemberAdmissionRecord(Tenant.ToString("D"), $"late-{i}", key,
                    RosterSigning.SignAdmission(f.Member, Tenant, $"late-{i}", key, "member", false, instant, nonce)));
            })
            .First(candidate => string.CompareOrdinal(revocation.SignatureB64Url, candidate.SignatureB64Url) < 0)
            .AttestReceipt(f.Founder, "founder", instant);
        await f.MergeRawAsync([revocation]);
        Assert.Contains(await f.StoredAsync(), r => r.RecordId == revocation.RecordId);
        await f.MergeRawAsync([late]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == late.RecordId);
        AssertSingleRefusal(await f.AuditsAsync(), "roster.record.chain_ineligible");
    }

    private static void AssertSingleRefusal(List<AuditRecord> audits, string code)
    {
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(Assert.Single(audits).Payload.Payload.Body));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    // ck-11 (DES-0029): a genuine founder-signed admission rewritten on the wire to carry a permission set is
    // refused, so the party gains no roster edge and the production gate refuses the members act the edge would
    // have admitted. The untampered control proves the same record and grant are otherwise allowed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TamperedPermissionEvidenceConfersNoAuthorityThroughTheGate(bool tampered)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "stray", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2));
        if (tampered)
        {
            candidate = JsonSerializer.Deserialize<RosterRecordCrdtState>(JsonSerializer.Serialize(candidate with
            {
                UnmappedWireFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Permissions"] = JsonSerializer.Deserialize<JsonElement>("[\"members:manage\"]"),
                },
            }))!;
        }
        await f.MergeRawAsync([candidate]);

        var tenant = new TenantId(Tenant.ToString("D"));
        var rosters = f.Provider.GetRequiredService<IVerifiedTenantRosterReader>();
        Assert.Equal(!tampered, (await rosters.ReadAsync(tenant, default)).Contains("stray"));
        // The subject holds a (non-Administrator) members grant; only the roster edge can satisfy RequireMember.
        var gate = Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(_ => true,
            new NodeAuthorizationRosterConstraintReader(new NoPartyBinding(), rosters),
            new RoleReference(RoleVocabularies.Domain, "member"));
        var decision = await gate.DecideAsync(new AuthorizationWriteContext(new ActorId("stray"), tenant, At.AddHours(3))
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", "stray"));
        Assert.Equal(tampered ? AuthorizationVerdict.Denied : AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal(!tampered, decision.Evidence.Roster!.Member);
    }

    private sealed class NoPartyBinding : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }

    private static Dictionary<string, JsonElement> PermissionField() => new(StringComparer.Ordinal)
    {
        ["Permissions"] = JsonSerializer.Deserialize<JsonElement>("[\"records:read\"]"),
    };

    private static RosterRecordCrdtState CopyReceipt(
        RosterRecordCrdtState target, RosterRecordCrdtState source) => target with
    {
        WireFormatVersion = source.WireFormatVersion,
        ReceivedAtIso = source.ReceivedAtIso,
        ReceivedByPartyId = source.ReceivedByPartyId,
        ReceivedByPublicKey = source.ReceivedByPublicKey,
        ReceiveAttestationSignatureB64Url = source.ReceiveAttestationSignatureB64Url,
    };

    private sealed class DelayedTrail : IAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await _inner.AppendAsync(record, ct);
        }
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => _inner.QueryAsync(query, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"roster-preinsert-{Guid.NewGuid():N}");
        private IAuditTrail _trail = null!;
        public Ed25519Signer Founder { get; } = new(KeyPair.Generate());
        public Ed25519Signer Member { get; } = new(KeyPair.Generate());
        public ServiceProvider Provider { get; private set; } = null!;
        public RosterCrdtProjection Projection => Provider.GetRequiredService<RosterCrdtProjection>();
        public IDbContextFactory<NodeLocalRosterDbContext> Factory => Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        // What the grant store holds for "member" - the replicated chain gates read authority from here now.
        private PermissionSet _memberAuthority = PermissionSet.Empty;

        private ServiceProvider NewProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False"));
            services.AddSingleton<IOperationSigner>(Founder);
            services.AddSingleton(_trail);
            services.AddAuthorizationRefusalAudit();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IRosterAuthority>(new TestRosterAuthority(("member", _memberAuthority)));
            services.AddNodeRoster();
            return services.BuildServiceProvider();
        }
        public static async Task<Fixture> CreateAsync(PermissionSet? permissions = null, IAuditTrail? trail = null)
        {
            var f = new Fixture { _trail = trail ?? new InMemoryAuditTrail(),
                _memberAuthority = permissions ?? PermissionSet.Empty };
            Directory.CreateDirectory(f._directory);
            f.Provider = f.NewProvider();
            await using (var db = await f.Factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            var roster = MemberRoster.Genesis(Tenant, "founder", f.Founder, Verifier, At, Guid.NewGuid())
                .Admit("founder", f.Founder, "member", f.Member.IssuerId, permissions ?? PermissionSet.Empty,
                    Verifier, At.AddMinutes(1), Guid.NewGuid());
            // Both records enter over the real inbound route, in reverse chain order, into an empty receiver.
            await f.MergeAsync(roster.EnumerateAdmissions().Reverse().Select(a => RosterRecordCrdtState.FromAdmission(a)));
            return f;
        }
        public RosterRecordCrdtState Admission(Ed25519Signer signer, string party, string target, DateTimeOffset? at = null)
        {
            var key = KeyPair.Generate().PrincipalId;
            var signed = RosterSigning.SignAdmission(signer, Tenant, target, key, party, false,
                at ?? At.AddHours(1), Guid.NewGuid());
            return RosterRecordCrdtState.FromAdmission(
                new MemberAdmissionRecord(Tenant.ToString("D"), target, key, signed));
        }
        public RosterRecordCrdtState Revocation(Ed25519Signer signer, string party, string target, DateTimeOffset? at = null) =>
            RosterRecordCrdtState.FromRevocation(new MemberRevocationRecord(Tenant.ToString("D"), target,
                RosterSigning.SignRevocation(signer, Tenant, target, party, at ?? At.AddHours(1), Guid.NewGuid())));
        public async Task MergeAsync(IEnumerable<RosterRecordCrdtState> records)
        {
            // A sender may persist arbitrary input; it never shares the receiving database.
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_directory, "sender.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            await using var sender = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(), factory,
                Verifier, Founder, NullLogger<RosterCrdtProjection>.Instance, attestationPartyId: "founder");
            foreach (var record in records) await sender.PublishLocalAsync(record, default);
            await sender.DrainPendingReconcilesAsync();
            var delta = await sender.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId, ReadOnlyMemory<byte>.Empty, default);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta!.Value, default);
        }
        public async Task MergeRawAsync(IEnumerable<RosterRecordCrdtState> records)
        {
            await using var raw = new Harborline.Api.Kernel.Crdt.CrdtProjection<RosterCrdtSchema>(
                new YDotNetCrdtEngine(), new RosterCrdtSchema(_ => Task.CompletedTask));
            raw.Mutate(schema => schema.PushMany(records));
            var delta = raw.EncodeDelta(ReadOnlyMemory<byte>.Empty);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta, default);
        }
        public async Task<List<RosterRecordCrdtState>> StoredAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return (await db.RosterRecords.AsNoTracking().ToListAsync()).Select(NodeRosterRecord.ToCrdtState).ToList();
        }
        public async Task<List<AuditRecord>> AuditsAsync()
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in _trail.QueryAsync(new AuditQuery(new TenantId(Tenant.ToString("D"))))) rows.Add(row);
            return rows;
        }
        public async Task RestartAsync()
        {
            await Provider.DisposeAsync();
            Provider = NewProvider();
        }
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
