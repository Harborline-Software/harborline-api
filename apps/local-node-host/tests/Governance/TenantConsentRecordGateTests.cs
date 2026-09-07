using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

/// <summary>
/// Ticket 213 slice 1 (ledger L646) — subject consent is a tenant LIFECYCLE RECORD, and the act that needs
/// it is decided by a gate predicate at its point of use, over the durable record, on the instant of the act.
/// </summary>
/// <remarks>
/// Three things are proved here and nowhere else: the transition table (valid AND invalid, refused before
/// anything is persisted), the point-of-use matrix over all six refusal shapes plus the one allow, and the
/// restart proof — every decision is re-read from disk, so dropping every process-local object leaves the
/// answer unchanged. Every case calls the gate DIRECTLY: there is no route, no HttpContext, no ambient
/// container read in any of them.
/// </remarks>
public sealed class TenantConsentRecordGateTests : IDisposable
{
    private static readonly TenantId Tenant = new("harborline-consent-t213");
    private static readonly SubjectId Subject = new("subject-alice");
    private static readonly SubjectId OtherSubject = new("subject-bob");
    private const string Purpose = "marketing-analytics";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "harborline-consent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp directory the OS still holds is not a test failure */ }
    }

    // ── The transition table ────────────────────────────────────────────────────────────────────────

    public static TheoryData<ConsentLifecycleState, string, bool> Transitions() => new()
    {
        { ConsentLifecycleState.Requested, "activate", true },
        { ConsentLifecycleState.Active, "expire", true },
        { ConsentLifecycleState.Active, "revoke", true },
        { ConsentLifecycleState.Requested, "expire", false },
        { ConsentLifecycleState.Requested, "revoke", false },
        { ConsentLifecycleState.Active, "activate", false },
        { ConsentLifecycleState.Expired, "activate", false },
        { ConsentLifecycleState.Expired, "expire", false },
        { ConsentLifecycleState.Expired, "revoke", false },
        { ConsentLifecycleState.Revoked, "activate", false },
        { ConsentLifecycleState.Revoked, "expire", false },
        { ConsentLifecycleState.Revoked, "revoke", false },
    };

    [Theory(DisplayName = "213 L646: the consent lifecycle admits requested->active->expired|revoked and nothing else")]
    [MemberData(nameof(Transitions))]
    public void The_transition_table_is_exactly_three_moves(
        ConsentLifecycleState from, string move, bool permitted)
    {
        var record = InState(from);

        if (permitted)
        {
            var next = Move(record, move);
            Assert.Equal(move switch
            {
                "activate" => ConsentLifecycleState.Active,
                "expire" => ConsentLifecycleState.Expired,
                _ => ConsentLifecycleState.Revoked,
            }, next.State);
            return;
        }

        var error = Assert.Throws<ConsentLifecycleTransitionException>(() => Move(record, move));
        Assert.Equal(from, error.From);
    }

    [Fact(DisplayName = "213 L646: an invalid transition is refused BEFORE anything is persisted")]
    public async Task An_invalid_transition_never_reaches_the_store()
    {
        var store = new CountingStore();
        var gate = new TenantConsentGate(store, new SucceedingAuditLog());
        var revoked = InState(ConsentLifecycleState.Revoked);

        await Assert.ThrowsAsync<ConsentLifecycleTransitionException>(
            () => gate.ActivateAsync(revoked, new ActorId("operator"), T0));

        Assert.Equal(0, store.Saves);
    }

    [Fact(DisplayName = "213 L646: every lifecycle transition writes an audit row naming the transition")]
    public async Task Every_transition_is_audited()
    {
        var store = new CountingStore();
        var audit = new CollectingAuditLog();
        var gate = new TenantConsentGate(store, audit);
        var actor = new ActorId("operator");

        var requested = await gate.RequestAsync(Requested(), actor);
        var active = await gate.ActivateAsync(requested, actor, T0.AddDays(1));
        await gate.RevokeAsync(active, actor, T0.AddDays(2));

        Assert.Equal(
            ["consent requested", "consent activated", "consent revoked"],
            audit.Appends.Select(a => a.Justification ?? string.Empty).ToArray());
        Assert.All(audit.Appends, a => Assert.Equal(Tenant, a.Tenant));
        Assert.All(audit.Appends, a => Assert.Equal("consent", a.EntityId.Scheme));
        Assert.Equal(3, store.Saves);
    }

    // ── The point-of-use matrix: six refusals and one allow ─────────────────────────────────────────

    [Fact(DisplayName = "213 L646: an active matching record allows the subject-consent act (direct call, no route)")]
    public async Task An_active_matching_record_allows()
    {
        var gate = await SeededAsync(Active());

        var decision = await gate.DecideAsync(Act(T0.AddDays(1)));

        Assert.True(decision.Allowed);
        Assert.Equal(ConsentRefusal.None, decision.Refusal);
        Assert.Equal("consent-1", decision.RecordId);
    }

    public static TheoryData<string, ConsentRefusal> Refusals() => new()
    {
        { "absent", ConsentRefusal.NoRecord },
        { "wrong-subject", ConsentRefusal.SubjectMismatch },
        { "wrong-purpose", ConsentRefusal.PurposeMismatch },
        { "out-of-scope", ConsentRefusal.OutOfScope },
        { "expired", ConsentRefusal.Expired },
        { "revoked", ConsentRefusal.Revoked },
        { "requested", ConsentRefusal.NotActive },
    };

    [Theory(DisplayName = "213 L646: the consent gate refuses absence, wrong subject, wrong purpose, out of scope, expired and revoked")]
    [MemberData(nameof(Refusals))]
    public async Task The_gate_refuses_every_non_matching_shape(string shape, ConsentRefusal expected)
    {
        var gate = shape switch
        {
            "absent" => await SeededAsync(),
            "wrong-subject" => await SeededAsync(Active() with { Subject = OtherSubject }),
            "wrong-purpose" => await SeededAsync(Active() with { Purpose = "product-research" }),
            "out-of-scope" => await SeededAsync(
                Active() with { Scope = ScopeExpression.Parse("/records/other-file") }),
            "expired" => await SeededAsync(Active().Expire(T0.AddHours(2))),
            "revoked" => await SeededAsync(Active().Revoke(T0.AddHours(2))),
            _ => await SeededAsync(Requested()),
        };

        var decision = await gate.DecideAsync(Act(T0.AddDays(1)));

        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Refusal);
    }

    [Fact(DisplayName = "213 L646: an active record whose window has closed refuses as expired without a sweep having run")]
    public async Task A_closed_window_expires_at_the_point_of_use()
    {
        var gate = await SeededAsync(Active() with { EffectiveUntil = T0.AddHours(6) });

        Assert.True((await gate.DecideAsync(Act(T0.AddHours(5)))).Allowed);
        Assert.Equal(ConsentRefusal.Expired, (await gate.DecideAsync(Act(T0.AddHours(7)))).Refusal);
    }

    // ── The restart proof ───────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "213 L646: the decision survives a restart — a brand-new store and gate over the same directory answer identically")]
    public async Task No_authority_depends_on_process_local_state()
    {
        // "Process" 1 — write the record through the gate, then drop every object it lived in.
        using (var store = FileTenantConsentStore.InDirectory(_dir))
        {
            var gate = new TenantConsentGate(store, new SucceedingAuditLog());
            var requested = await gate.RequestAsync(Requested(), new ActorId("operator"));
            await gate.ActivateAsync(requested, new ActorId("operator"), T0);
        }

        // "Process" 2 — nothing carried over but the directory.
        using (var store = FileTenantConsentStore.InDirectory(_dir))
        {
            var gate = new TenantConsentGate(store, new SucceedingAuditLog());
            var decision = await gate.DecideAsync(Act(T0.AddDays(1)));
            Assert.True(decision.Allowed);
            Assert.Equal("consent-1", decision.RecordId);

            // And the revocation is just as durable as the consent.
            var record = Assert.Single(await store.ReadAsync(Tenant));
            await gate.RevokeAsync(record, new ActorId("operator"), T0.AddDays(2));
        }

        using (var store = FileTenantConsentStore.InDirectory(_dir))
        {
            var gate = new TenantConsentGate(store, new SucceedingAuditLog());
            Assert.Equal(ConsentRefusal.Revoked, (await gate.DecideAsync(Act(T0.AddDays(3)))).Refusal);
        }
    }

    // ── The composed node, and the enforcer that consumes the gate ──────────────────────────────────

    [Fact(DisplayName = "213 L646: the composed node resolves the record-backed consent gate, not the deny-all default")]
    public void The_node_composition_wires_the_record_gate()
    {
        var services = new ServiceCollection();
        services.AddTestAuthorizationGate();
        services.AddNodeTenantGovernance(_dir);
        RecoverySubstrate(services);
        services.AddTestNodeForms();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<TenantConsentGate>(sp.GetRequiredService<IConsentGate>());
        Assert.IsType<FileTenantConsentStore>(sp.GetRequiredService<ITenantConsentStore>());
    }

    [Fact(DisplayName = "213 L646: the field-policy enforcer refuses a consent-effect read with the gate's own reason, and allows it once the record is active")]
    public async Task The_enforcer_reads_the_record_at_its_point_of_use()
    {
        var services = new ServiceCollection();
        services.AddTestAuthorizationGate();
        services.AddNodeTenantGovernance(_dir);
        RecoverySubstrate(services);
        services.AddTestNodeForms();
        using var sp = services.BuildServiceProvider();

        var enforcer = sp.GetRequiredService<IFieldPolicyEnforcer>();
        var gate = sp.GetRequiredService<TenantConsentGate>();
        var entity = new EntityId("harborline", "node", "contact-file");
        var ctx = new ReadFieldContext(
            ConsentPolicy(), "0412 000 000", Tenant, new ActorId("operator"), entity, [], Subject);

        // Nothing on file: the enforcer refuses, carrying the gate's own reason.
        var refused = await Assert.ThrowsAsync<ConsentRequiredException>(() => enforcer.ProjectForReadAsync(ctx));
        Assert.Equal(ConsentRefusal.NoRecord, refused.Refusal);

        // The record the enforcer will read is scoped to the very record the act addresses.
        var record = TenantConsentRecord.Request(
            "consent-enforcer", Tenant, Subject, Purpose,
            FieldPolicyEnforcer.ConsentScope(entity), T0, signatureConsentRecordId: "sig-consent-9");
        var requested = await gate.RequestAsync(record, new ActorId("operator"));

        // Requested is not consent: the enforcer still refuses, and says so precisely.
        var stillRefused = await Assert.ThrowsAsync<ConsentRequiredException>(
            () => enforcer.ProjectForReadAsync(ctx));
        Assert.Equal(ConsentRefusal.NotActive, stillRefused.Refusal);

        var active = await gate.ActivateAsync(requested, new ActorId("operator"), T0);
        var projection = await enforcer.ProjectForReadAsync(ctx);
        Assert.True(projection.Readable);
        Assert.Equal("0412 000 000", projection.Value);

        // The legal-evidence reconciliation is BY REFERENCE: the tenant record names the signature-specific
        // ConsentRecord and nothing here reads, copies or weakens it.
        Assert.Equal("sig-consent-9", active.SignatureConsentRecordId);

        await gate.RevokeAsync(active, new ActorId("operator"), T0.AddDays(1));
        var afterRevoke = await Assert.ThrowsAsync<ConsentRequiredException>(
            () => enforcer.ProjectForReadAsync(ctx));
        Assert.Equal(ConsentRefusal.Revoked, afterRevoke.Refusal);
    }

    [Fact(DisplayName = "213 L646: with no durable consent store wired the one gate refuses every act, and refuses to pretend a transition happened")]
    public async Task The_default_store_is_fail_closed()
    {
        var gate = new TenantConsentGate(new NoConsentRecordsStore(), new SucceedingAuditLog());

        var decision = await gate.DecideAsync(Act(T0));
        Assert.False(decision.Allowed);
        Assert.Equal(ConsentRefusal.NoRecord, decision.Refusal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RequestAsync(Requested(), new ActorId("operator")));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static void RecoverySubstrate(IServiceCollection services)
    {
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
    }

    private static ResolvedFieldPolicy ConsentPolicy() => new(
        "phone",
        [],
        new Dictionary<Trigger, IReadOnlyList<PolicyEffect>>
        {
            [Trigger.Read] =
            [
                new PolicyEffect(EffectKind.Consent, [Trigger.Read], new EffectParams(ConsentPurpose: Purpose)),
            ],
        },
        new ResolvedAspect("phone", [], null, null, [], null, null, Immutability.Mutable, null, null));

    private static ScopeExpression Scope { get; } = ScopeExpression.Parse("/records/contact-file");

    private static ConsentRequest Act(DateTimeOffset at) => new(Tenant, Subject, Purpose, Scope, at);

    private static TenantConsentRecord Requested() =>
        TenantConsentRecord.Request("consent-1", Tenant, Subject, Purpose, Scope, T0);

    private static TenantConsentRecord Active() => Requested().Activate(T0);

    private static TenantConsentRecord InState(ConsentLifecycleState state) => state switch
    {
        ConsentLifecycleState.Requested => Requested(),
        ConsentLifecycleState.Active => Active(),
        ConsentLifecycleState.Expired => Active().Expire(T0.AddHours(1)),
        _ => Active().Revoke(T0.AddHours(1)),
    };

    private static TenantConsentRecord Move(TenantConsentRecord record, string move) => move switch
    {
        "activate" => record.Activate(T0.AddHours(2)),
        "expire" => record.Expire(T0.AddHours(2)),
        _ => record.Revoke(T0.AddHours(2)),
    };

    private async Task<TenantConsentGate> SeededAsync(params TenantConsentRecord[] records)
    {
        var store = FileTenantConsentStore.InDirectory(_dir);
        foreach (var record in records)
        {
            await store.SaveAsync(record);
        }

        return new TenantConsentGate(store, new SucceedingAuditLog());
    }

    private sealed class CountingStore : ITenantConsentStore
    {
        private readonly List<TenantConsentRecord> _rows = [];

        public int Saves { get; private set; }

        public ValueTask<IReadOnlyList<TenantConsentRecord>> ReadAsync(
            TenantId tenant, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TenantConsentRecord>>(_rows);

        public ValueTask<IReadOnlyList<TenantId>> TenantsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TenantId>>(
                _rows.Select(r => r.Tenant).Distinct().ToArray());

        public ValueTask SaveAsync(TenantConsentRecord record, CancellationToken ct = default)
        {
            Saves++;
            _rows.RemoveAll(r => r.Id == record.Id);
            _rows.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private class SucceedingAuditLog : IAuditLog
    {
        public virtual Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default) =>
            Task.FromResult(new AuditId(1));

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class CollectingAuditLog : SucceedingAuditLog
    {
        public List<AuditAppend> Appends { get; } = [];

        public override Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default)
        {
            Appends.Add(append);
            return base.AppendAsync(append, ct);
        }
    }
}
