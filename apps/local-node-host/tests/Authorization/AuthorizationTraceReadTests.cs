using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 212 slice 3 (ledger L651/L652) — the authorized first-class production read of one recorded
/// decision's four-step trace and its counterfactual. Every case goes through the REAL
/// <see cref="AuthorizationGate"/> over the real seeded definitions and the real grant store, so an allow
/// or a refusal here is the production resolution's, and the trace returned is the one the deciding path
/// stored with the audit entry rather than anything re-evaluated at read time.
/// </summary>
public sealed class AuthorizationTraceReadTests
{
    private static readonly TenantId Tenant = new("tenant-212c");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
    private static readonly ActorId Auditor = new("auditor-212c");
    private static readonly ActorId Stranger = new("stranger-212c");

    [Fact]
    public async Task PreDecisionFloorRefusalIsStoredAndReadableOnlyByAuditor()
    {
        await using var h = await Harness.CreateAsync();
        var audit = new AuthorizationRefusalAudit(h.Trail, new Ed25519Signer(KeyPair.Generate()),
            NullLogger<AuthorizationRefusalAudit>.Instance);
        var refusal = new AuthorizationRefusal(MemberRoster.NoBrickingFloorCode, "Roster revocation refused",
            "Revoking founder would remove the last signed and live authority floor holder.",
            "Admit a signed successor that holds the floor live.", "classified-record-evidence");
        var id = await audit.RecordAsync(refusal, Permission.MembersRevoke, h.Subject, Tenant, At, null);
        Assert.NotNull(id);
        var read = await h.Reader.ReadAsync(Tenant, Auditor, id.Value, At);
        Assert.Equal(AuthorizationTraceAvailability.PreDecisionRefusal, read.Availability);
        Assert.Equal(new AuthorizationPreDecisionRefusal(refusal.Code, refusal.Detail, refusal.Remediation), read.Refusal);
        Assert.Empty(read.Steps);
        Assert.Null(read.Counterfactual);
        Assert.DoesNotContain("classified-record-evidence", System.Text.Json.JsonSerializer.Serialize(read));
        var denied = await h.Reader.ReadAsync(Tenant, Stranger, id.Value, At);
        Assert.Equal(AuthorizationTraceAvailability.Refused, denied.Availability);
        Assert.Null(denied.Refusal);
        var missing = await h.Reader.ReadAsync(Tenant, Stranger, Guid.NewGuid(), At);
        Assert.Equal(denied, missing);

        // The durable audit reader rematerializes object values as JsonElement, not CLR records.
        var rows = new List<AuditRecord>();
        await foreach (var row in h.Trail.QueryAsync(new AuditQuery(Tenant))) rows.Add(row);
        var original = Assert.Single(rows);
        var reloaded = original with
        {
            AuditId = Guid.NewGuid(),
            Payload = original.Payload with
            {
                Payload = new AuditPayload(original.Payload.Payload.Body.ToDictionary(p => p.Key,
                    p => (object?)System.Text.Json.JsonSerializer.SerializeToElement(p.Value)))
            }
        };
        Assert.True(new Ed25519Verifier().Verify(reloaded.Payload));
        await h.Trail.AppendAsync(reloaded);
        Assert.Equal(read.Refusal, (await h.Reader.ReadAsync(Tenant, Auditor, reloaded.AuditId, At)).Refusal);
    }

    /// <summary>The subject of the decision reads their own trace: ticket 163's question, in the first
    /// person. What comes back is exactly what was stored — steps and counterfactual — and nothing else.</summary>
    [Fact(DisplayName = "The person the decision was about reads their own four steps and counterfactual")]
    public async Task OwnDecision_ReturnsTheStoredProjectionAndCounterfactual()
    {
        await using var h = await Harness.CreateAsync();
        var (auditId, decision) = await h.RecordAnAuthorizedActAsync();

        var read = await h.Reader.ReadAsync(Tenant, h.Subject, auditId, At);

        Assert.Equal(AuthorizationTraceAvailability.Available, read.Availability);
        Assert.Equal(AuthorizationDecisionEvidence.CurrentVersion, read.Version);
        Assert.Equal(
            decision.Evidence.Project().Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))),
            read.Steps.Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))));
        var derived = AuthorizationCounterfactual.From(decision.Evidence);
        Assert.Equal(derived.Kind.ToString(), read.Counterfactual!.Kind);
        Assert.Equal(derived.Description, read.Counterfactual.Description);
        // Never a secret: the entry's signed payload and its signature are not in what a reader gets back.
        var text = string.Join("|", read.Steps.SelectMany(step => step.Facts)) + read.Counterfactual.Description;
        Assert.DoesNotContain(h.PayloadSignature, text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An OS party reads its own trace through a case-variant account spelling")]
    public async Task OwnDecision_CaseVariantOsParty_ReturnsTheStoredTrace()
    {
        await using var h = await Harness.CreateAsync(new ActorId("os:Chris#a1b2c3d4"));
        var (auditId, decision) = await h.RecordAnAuthorizedActAsync();

        var read = await h.Reader.ReadAsync(Tenant, new ActorId("os:chris#a1b2c3d4"), auditId, At);

        Assert.Equal(AuthorizationTraceAvailability.Available, read.Availability);
        Assert.Equal(AuthorizationDecisionEvidence.CurrentVersion, read.Version);
        Assert.Equal(
            decision.Evidence.Project().Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))),
            read.Steps.Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))));
        var derived = AuthorizationCounterfactual.From(decision.Evidence);
        Assert.Equal(derived.Kind.ToString(), read.Counterfactual!.Kind);
        Assert.Equal(derived.Description, read.Counterfactual.Description);
    }

    /// <summary>Another person's trace is a read of the audit trail about them. A member holding
    /// <c>audit:trace-read</c> is refused it; the Auditor, holding <c>audit:read</c>, is not.</summary>
    [Fact(DisplayName = "Another person's trace refuses unless the caller holds audit:read")]
    public async Task AnotherPersonsDecision_RefusesUnlessAuditor()
    {
        await using var h = await Harness.CreateAsync();
        var (auditId, _) = await h.RecordAnAuthorizedActAsync();

        var stranger = await h.Reader.ReadAsync(Tenant, Stranger, auditId, At);
        var auditor = await h.Reader.ReadAsync(Tenant, Auditor, auditId, At);

        Assert.Equal(AuthorizationTraceAvailability.Refused, stranger.Availability);
        Assert.Empty(stranger.Steps);
        Assert.Null(stranger.Counterfactual);
        Assert.Equal(AuthorizationTraceAvailability.Available, auditor.Availability);
        Assert.Equal(AuthorizationDecisionEvidence.StepCount, auditor.Steps.Count);
    }

    /// <summary>An entry appended before ticket 212 — or by an ordinary, unauthorized append — carries no
    /// evidence. The answer is "not recorded", not an exception and not an empty allow.</summary>
    [Fact(DisplayName = "An entry with no evidence answers not-available rather than erroring")]
    public async Task AnEntryWithNoEvidence_AnswersNotAvailable()
    {
        await using var h = await Harness.CreateAsync();
        var record = await h.UnauthorizedRecordAsync();
        await h.Trail.AppendAsync(record);

        var read = await h.Reader.ReadAsync(Tenant, Auditor, record.AuditId, At);

        Assert.Equal(AuthorizationTraceAvailability.NotAvailable, read.Availability);
        Assert.Empty(read.Steps);
        Assert.Null(read.Counterfactual);
        Assert.Null(read.Version);
    }

    /// <summary>An entry that is not there refuses a caller who may not read it, in the same words as one
    /// that is — the refusal discloses nothing about which.</summary>
    [Fact(DisplayName = "A missing entry refuses a caller who holds no audit:read, disclosing nothing")]
    public async Task AMissingEntry_RefusesWithoutDisclosingItsAbsence()
    {
        await using var h = await Harness.CreateAsync();
        var (present, _) = await h.RecordAnAuthorizedActAsync();

        var missing = await h.Reader.ReadAsync(Tenant, Stranger, Guid.NewGuid(), At);
        var existing = await h.Reader.ReadAsync(Tenant, Stranger, present, At);

        Assert.Equal(existing, missing);
        Assert.Equal(AuthorizationTraceAvailability.Refused, missing.Availability);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Ed25519Signer _signer;
        private readonly KeyPair _keys;

        private Harness(ServiceProvider provider, KeyPair keys, Ed25519Signer signer, ActorId? subject)
        {
            _provider = provider;
            _keys = keys;
            _signer = signer;
            Subject = subject ?? new ActorId(signer.IssuerId.ToBase64Url());
            // The REAL gate over the definition-joined closure. Constructed rather than resolved for the
            // same reason AuditorSingleCapabilityTests constructs one: the container's registered gate is
            // the writer's allow-all authority and would allow every read.
            Gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            Trail = new InMemoryAuditTrail();
            Reader = new AuthorizationTraceReader(Trail, Gate);
        }

        public ActorId Subject { get; }
        public AuthorizationGate Gate { get; }
        public InMemoryAuditTrail Trail { get; }
        public AuthorizationTraceReader Reader { get; }
        public string PayloadSignature { get; private set; } = "unset";

        public static async Task<Harness> CreateAsync(ActorId? subject = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            var keys = KeyPair.Generate();
            var harness = new Harness(provider, keys, new Ed25519Signer(keys), subject);
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(Tenant, At, AuthorizationSeedProfile.Production);
            var grants = provider.GetRequiredService<IGrantStore>();
            // The subject and the stranger are ordinary members; the auditor is the sealed platform role.
            await grants.AppendAsync(Tenant, Grant(harness.Subject, AccessGrantAuthorizationSeed.MemberRole));
            await grants.AppendAsync(Tenant, Grant(Stranger, AccessGrantAuthorizationSeed.MemberRole));
            await grants.AppendAsync(Tenant, Grant(Auditor, RoleReference.Auditor));
            return harness;
        }

        /// <summary>Decides one real act for the subject and records it through the authorized append, so
        /// the entry carries the evidence and counterfactual the deciding path produced.</summary>
        public async Task<(Guid AuditId, AuthorizationDecision Decision)> RecordAnAuthorizedActAsync()
        {
            var operation = AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);
            var request = new AuthorizationWriteContext(Subject, Tenant, At)
                .Request(operation, AuthorizationGate.RecordKindFor(operation), "a");
            var decision = await Gate.DecideAsync(request);
            Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);

            var payload = await _signer.SignAsync(
                new AuditPayload(new Dictionary<string, object?> { ["act"] = "write" }), At, Guid.NewGuid());
            PayloadSignature = payload.Signature.ToBase64Url();
            var record = new AuditRecord(
                Guid.NewGuid(), Tenant, AuditEventType.PaymentAuthorized, At, payload,
                Array.Empty<AttestingSignature>(),
                Actor: Subject, Target: request.Target, Act: request.Act);
            await Trail.AppendAuthorizedAsync(record, decision);
            return (record.AuditId, decision);
        }

        /// <summary>An ordinary append: no decision, so no evidence — the pre-212 shape.</summary>
        public async Task<AuditRecord> UnauthorizedRecordAsync()
        {
            var payload = await _signer.SignAsync(
                new AuditPayload(new Dictionary<string, object?> { ["act"] = "note" }), At, Guid.NewGuid());
            return new AuditRecord(
                Guid.NewGuid(), Tenant, AuditEventType.PaymentAuthorized, At, payload,
                Array.Empty<AttestingSignature>());
        }

        private static AccessGrant Grant(ActorId subject, RoleReference role) => new(
            GrantId.New(), Tenant, subject, role, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(At.AddHours(-1)), GranterKind.Person, new ActorId("tenant-admin"),
            At.AddHours(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("tenant-admin")), At.AddHours(-1));

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            _keys.Dispose();
        }
    }
}
