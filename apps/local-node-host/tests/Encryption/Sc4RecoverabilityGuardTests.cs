using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Reports.DependencyInjection;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Docs;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Payroll;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Encryption;

/// <summary>
/// ADR 0115 SC-4 amendment — tests for the fail-closed recoverability guard
/// (<see cref="Sc4RecoverabilityGuard"/>) and the host-side half of the gating
/// <b>SC4-T9</b> rebuildable-proof (SPOT-CHECK verdict
/// <c>council-verdict-2026-06-14-sc4-impl-spot-check.md</c>, M1 part (b) + S1;
/// evolved for Cohort D Step 2a by <c>council-verdict-security-engineering-sc4c2-node-posting-2026-06-15</c>).
/// </summary>
/// <remarks>
/// <para>
/// The Rust shell holds SC4-T9 part (a) — a passphrase recovery on a new seed
/// recovers the same Store DEK, so the relational financial store (<c>local-node.db</c>)
/// opens. This file holds part (b): the per-team KV/event store holds <b>no
/// irreplaceable financial value</b>.
/// </para>
/// <para>
/// <b>Cohort D Step 2a gate evolution (SC4-C2 verdict 2026-06-15, Q3).</b> The financial
/// posting path is now wired on the node, and the SC4-C2 analysis proved it writes ONLY the
/// recoverable, Store-DEK-enveloped <c>local-node.db</c> — never the seed-keyed per-team kernel
/// event log. So SC4-T9(b) changed from the coarse "the host references NO posting type" to a
/// <b>conditioned</b> gate: the host MAY reference posting types ONLY IF the recoverability
/// conditions (a)-(d) hold. Two complementary layers enforce that:
/// <list type="bullet">
///   <item><b>Layer 1</b> (<see cref="HostReferencesNoKernelCrdtWriter"/>) — the IL TypeRef scan,
///     INVERTED: posting types are now ALLOWED; the kernel CRDT writer
///     (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and the per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> are FORBIDDEN. That is the orphan vector — the
///     only path that would drive financial value into the non-recoverable per-team store.</item>
///   <item><b>Layer 2</b> (<see cref="PostingDiGraph_SatisfiesSc4C2Conditions"/>) — a runtime
///     DI-graph assertion over the REAL posting composition (<c>AddNodeFinancialPosting</c>, the
///     same method <c>Program.cs</c> calls) confirming conditions (a)/(b)/(d): <c>IJournalStore</c>
///     resolves to <see cref="NodeEfJournalStore"/>; no non-recoverable <c>IDomainEventStore</c> /
///     per-team <c>IEventLog</c> is registered; the resolvers resolve to the node EF reads.</item>
/// </list>
/// The S1 runtime boot guard + multi-team interaction are UNCHANGED (the verdict confirms posting
/// does not enlarge the orphan surface the S1 guard already covers).
/// </para>
/// </remarks>
public sealed class Sc4RecoverabilityGuardTests
{
    private static LocalNodeOptions Sc4Options(bool? multiTeam)
    {
        var opts = new LocalNodeOptions
        {
            // A 64-char hex string => SC-4 envelope recovery is "active".
            StoreDekHex = new string('a', 64),
        };
        if (multiTeam is not null)
        {
            opts.MultiTeam = new MultiTeamOptions { Enabled = multiTeam.Value };
        }
        return opts;
    }

    // ── S1 guard: fires on the drift trap ────────────────────────────────────

    [Fact(DisplayName = "S1: SC-4 active + multi-team ENABLED + KV not envelope-extended → fail closed")]
    public void Sc4Active_MultiTeamEnabled_KvNotExtended_FailsClosed()
    {
        var ex = Assert.Throws<Sc4RecoverabilityViolationException>(() =>
            Sc4RecoverabilityGuard.Validate(Sc4Options(multiTeam: true), perTeamKvStoreIsEnvelopeExtended: false));

        // The message must name the orphaning vector + an actionable resolution.
        Assert.Contains("MultiTeam", ex.Message);
        Assert.Contains("orphan", ex.Message);
        Assert.Contains("MultiTeam:Enabled=false", ex.Message);
    }

    [Fact(DisplayName = "S1: SC-4 active + multi-team UNSET (host default true) + KV not extended → fail closed")]
    public void Sc4Active_MultiTeamUnset_DefaultsTrue_FailsClosed()
    {
        // The latent drift trap: the host default for MultiTeam.Enabled is TRUE
        // (Wave 6.7). A LocalNodeOptions whose MultiTeam is the default instance is
        // therefore "effectively enabled" and MUST fail closed under SC-4 recovery.
        var defaultOptions = new LocalNodeOptions { StoreDekHex = new string('b', 64) };
        Assert.True(defaultOptions.MultiTeam.Enabled, "host default MultiTeam.Enabled must be true (Wave 6.7) — the drift trap this guard defends");

        Assert.Throws<Sc4RecoverabilityViolationException>(() =>
            Sc4RecoverabilityGuard.Validate(defaultOptions, perTeamKvStoreIsEnvelopeExtended: false));
    }

    // ── S1 guard: no-ops where recoverability is intact ──────────────────────

    [Fact(DisplayName = "S1: SC-4 active + multi-team DISABLED (the shell-pinned single-device posture) → allowed")]
    public void Sc4Active_MultiTeamDisabled_Allowed()
    {
        // The single-device v1 posture the Tauri sidecar pins: MultiTeam=false.
        var ex = Record.Exception(() =>
            Sc4RecoverabilityGuard.Validate(Sc4Options(multiTeam: false), perTeamKvStoreIsEnvelopeExtended: false));
        Assert.Null(ex);
    }

    [Fact(DisplayName = "S1: NO Store DEK injected (legacy seed-keyed path) → guard is a no-op even with multi-team on")]
    public void NoStoreDek_LegacyPath_IsNoOp()
    {
        // The Bridge-spawned-tenant / non-SC-4 path keys the store HKDF(rootSeed).
        // There is no passphrase reseed, so there is no orphaning vector — the
        // guard must NOT fire even when multi-team is enabled.
        var legacy = new LocalNodeOptions { StoreDekHex = null, MultiTeam = new MultiTeamOptions { Enabled = true } };
        var ex = Record.Exception(() =>
            Sc4RecoverabilityGuard.Validate(legacy, perTeamKvStoreIsEnvelopeExtended: false));
        Assert.Null(ex);
    }

    [Fact(DisplayName = "S1: SC-4 active + multi-team on but KV store IS envelope-extended → allowed (recovery restores KV too)")]
    public void Sc4Active_MultiTeamEnabled_KvEnvelopeExtended_Allowed()
    {
        // The deferred SC4-C2 option-b future: if the per-team KV store is itself
        // behind the recoverable envelope, a passphrase recovery restores it too,
        // so multi-team is safe under SC-4. The guard lifts.
        var ex = Record.Exception(() =>
            Sc4RecoverabilityGuard.Validate(Sc4Options(multiTeam: true), perTeamKvStoreIsEnvelopeExtended: true));
        Assert.Null(ex);
    }

    [Fact(DisplayName = "S1: null options → ArgumentNullException (fail-closed on a malformed call)")]
    public void NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Sc4RecoverabilityGuard.Validate(null!, perTeamKvStoreIsEnvelopeExtended: false));
    }

    // ── SC4-T9(b) Layer 1: the host references no kernel CRDT writer (the orphan vector) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 1 — INVERTED IL TypeRef scan (Cohort D Step 2a; SC4-C2 verdict 2026-06-15, Q3).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before Cohort D Step 2a this gate asserted the host references NO financial posting type —
    /// a coarse proxy for "no financial value reaches the seed-keyed per-team kernel event log." The
    /// SC4-C2 analysis proved that proxy too coarse: the node posting path writes ONLY the recoverable
    /// <c>local-node.db</c> via <c>IJournalStore</c> (Q1), never the per-team
    /// <c>FileBackedEventLog</c>. So the scan is INVERTED — posting types are now ALLOWED, and the
    /// ACTUAL orphan vector is forbidden instead: the kernel CRDT writer
    /// (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and the per-team
    /// <c>FileBackedEventLog</c>/<c>IEventLog</c>. Those are the only types whose IL presence in the
    /// host's own composition would mean financial value could be driven into the
    /// <c>HKDF(rootSeed, teamId)</c>-keyed store a passphrase reseed would orphan.
    /// </para>
    /// <para>
    /// <b>Why the Encryption namespace is NOT forbidden:</b> the host legitimately references
    /// <c>Harborline.Api.Foundation.LocalFirst.Encryption</c> for SQLCipher key derivation on
    /// <c>local-node.db</c> (Q1 / verdict Q3 note). The gate keys on the LEDGER WRITER types, not the
    /// encryption keystore primitives.
    /// </para>
    /// <para>
    /// <b>Non-vacuous:</b> belt+suspenders A asserts the host still references the recoverable
    /// schema + the <c>IJournalStore</c> seam (so the scan reads a populated table), and
    /// belt+suspenders B asserts the host DOES now reference the posting service (proving the
    /// inversion really took effect — the posting wiring is live, not absent). Paired with the
    /// Layer-2 runtime DI-graph assertion below + the Rust SC4-T9 part (a).
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 1: the host references NO kernel CRDT writer (posting types ALLOWED; PostingEngine/ILedgerEventStream/FileBackedEventLog/IEventLog FORBIDDEN)")]
    public void HostReferencesNoKernelCrdtWriter()
    {
        var hostAssembly = typeof(LocalNodeOptions).Assembly;

        // The kernel CRDT-writer types — the ONLY path that drives financial value into the
        // seed-keyed, NON-recoverable per-team kernel event log. If the host's own IL ever names
        // one of these, financial posting could orphan value on a passphrase reseed → re-run the
        // SC4-C2 recoverability analysis. (security-engineering SC4-C2 verdict 2026-06-15, Q3
        // forbidden-set.)
        (string Ns, string Name)[] forbiddenKernelCrdtWriterTypes =
        [
            ("Harborline.Api.Kernel.Ledger", "PostingEngine"),        // the concrete CRDT writer into the event log
            ("Harborline.Api.Kernel.Ledger", "ILedgerEventStream"),   // its write seam
            ("Harborline.Api.Kernel.Events", "FileBackedEventLog"),   // the per-team seed-keyed event log file
            ("Harborline.Api.Kernel.Events", "IEventLog"),            // the per-team event-log interface (defence-in-depth)
        ];

        var referencedTypes = ReferencedTypeNames(hostAssembly);

        var leaked = forbiddenKernelCrdtWriterTypes
            .Where(t => referencedTypes.Contains((t.Ns, t.Name)))
            .Select(t => $"{t.Ns}.{t.Name}")
            .ToArray();
        Assert.True(
            leaked.Length == 0,
            "SC4-T9(b) Layer 1 VIOLATED: the local-node host references kernel CRDT-writer type(s) [" +
            string.Join(", ", leaked) + "]. These drive financial value into the per-team kernel event " +
            "log (FileBackedEventLog, keyed HKDF(rootSeed, teamId)), which a passphrase recovery (new seed) " +
            "would silently orphan. The node posting path is permitted ONLY because it writes the recoverable " +
            "local-node.db via IJournalStore — re-run the SC4-C2 recoverability analysis: keep the kernel CRDT " +
            "writer out of the host, or extend the per-team KV store behind the recoverable Store-DEK envelope " +
            "(SC4-C2 option b) and lift the SC4 recoverability guard accordingly.");

        // Belt + suspenders A: the host MUST reference the *.Data EF-schema namespace + the
        // recoverable IJournalStore seam (so this test cannot pass vacuously if the metadata scan
        // ever reads nothing).
        Assert.Contains(referencedTypes, t => t.Ns == "Harborline.Api.Blocks.FinancialLedger.Data");
        Assert.Contains(referencedTypes, t => t == ("Harborline.Api.Blocks.FinancialLedger.Services", "IJournalStore"));

        // Belt + suspenders B: Cohort D Step 2a wires the posting service — assert the host DOES now
        // reference JournalPostingService. This proves the INVERSION really took effect: the posting
        // path is live (the old coarse gate would have FAILED on this exact reference), and the gate
        // still permits it because the orphan-vector kernel-writer types above are absent.
        Assert.Contains(
            referencedTypes,
            t => t == ("Harborline.Api.Blocks.FinancialLedger.Services", "JournalPostingService"));

        // Belt + suspenders C (ADR 0126 T4 — SE-1): the node audit system-of-record flip is wired.
        // The audit types are DEFINED IN the host assembly (not a separate package), so they appear in
        // the host's TypeDefinitions, not its TypeReferences — assert their PRESENCE as defined types.
        // This proves the audit composition is live in the host (the Layer-2 audit DI-graph test below
        // proves it satisfies SC4-C2). The forbidden-set scan above covers the audit path too: the audit
        // enlister + reader take NO IEventLog dependency, so a future edit injecting IEventLog into the
        // audit path would surface IEventLog in the host's TypeReferences and fail the forbidden-set
        // assertion above (IEventLog lives in the separate Harborline.Api.Kernel.Events package → it IS a
        // cross-assembly TypeRef when referenced).
        var hostDefinedTypes = hostAssembly.GetTypes()
            .Select(t => t.FullName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Harborline.Api.LocalNodeHost.Data.Audit.NodeAuditEventReader", hostDefinedTypes);
        Assert.Contains("Harborline.Api.LocalNodeHost.Data.Audit.NodeAuditWriteEnlister", hostDefinedTypes);
        Assert.Contains("Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule", hostDefinedTypes);
    }

    // ── SC4-T9(b) Layer 2: runtime DI-graph assertion over the REAL posting composition ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 — runtime DI-graph assertion (Cohort D Step 2a; SC4-C2 verdict 2026-06-15, Q3).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The IL TypeRef scan (Layer 1) cannot see a service registered INSIDE an opaque third-party DI
    /// extension. Now that posting is wired, this Layer-2 assertion builds the REAL posting
    /// composition (<see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/> — the SAME
    /// method <c>Program.cs</c> calls, over the same recoverable <see cref="NodeEfJournalStore"/>) and
    /// confirms the SC4-C2 conditions hold at the resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a)</b> <c>IJournalStore</c> resolves to <see cref="NodeEfJournalStore"/> (recoverable
    ///     <c>local-node.db</c>) — the posting service's only persistence sink.</item>
    ///   <item><b>(b)</b> NO non-recoverable cross-cluster event bus is wired: the host never calls
    ///     <c>AddFoundationEvents()</c>, so no <c>IDomainEventStore</c> is registered (Noop publisher ⇒
    ///     safe). If a future wave wires it, this assertion fires and forces the connection-target check.</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the entity/posting resolvers resolve to the node EF reads
    ///     (<see cref="NodeEfAccountResolver"/> / <see cref="NodeEfPeriodResolver"/>) over
    ///     <c>local-node.db</c>, never a seed-keyed KV store.</item>
    /// </list>
    /// <para>
    /// <b>Non-vacuous:</b> the composition is the production one, and the assertions are POSITIVE
    /// (IJournalStore IS NodeEfJournalStore; resolvers ARE the node EF types) plus NEGATIVE (no
    /// IDomainEventStore / IEventLog). A drift that pointed IJournalStore at a non-recoverable store,
    /// or wired a non-recoverable IDomainEventStore, would fail this immediately.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2: the built posting DI-graph satisfies SC4-C2 (a)/(b)/(d) — IJournalStore is NodeEfJournalStore, no event-store/event-log, resolvers are local EF")]
    public void PostingDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodePostingProvider();

        // (a) IJournalStore resolves to the recoverable node store.
        Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());

        // (b) No cross-cluster domain-event store is registered (AddFoundationEvents was NOT called).
        // A null IDomainEventStore ⇒ the publisher is the cluster-default Noop ⇒ posting writes no
        // event-bus value at all, recoverable or otherwise.
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope — the per-team
        // FileBackedEventLog lives inside each TeamContext.Services, never the root container, and
        // the posting composition does not register one.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // (d) The account + period resolvers resolve to the node EF reads over local-node.db.
        Assert.IsType<NodeEfAccountResolver>(provider.GetRequiredService<IAccountResolver>());
        Assert.IsType<NodeEfPeriodResolver>(provider.GetRequiredService<IPeriodResolver>());

        // Sanity: the posting service itself resolves over the above (proves the graph is buildable).
        Assert.IsType<JournalPostingService>(provider.GetRequiredService<IJournalPostingService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for Layer 2.</b> A deliberately MISCONFIGURED composition — IJournalStore
    /// pointed at the in-memory (non-node) store instead of <see cref="NodeEfJournalStore"/> — must
    /// FAIL the condition-(a) assertion. This demonstrates the Layer-2 check is real: it does not pass
    /// for any arbitrary graph, only for one that satisfies SC4-C2 (a).
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (non-vacuous): a misconfigured IJournalStore (NOT NodeEfJournalStore) FAILS condition (a)")]
    public void PostingDiGraph_MisconfiguredJournalStore_FailsConditionA()
    {
        var services = new ServiceCollection();
        // Register the SAME EF factory + resolvers, but DELIBERATELY wire IJournalStore to the
        // in-memory store (a non-recoverable, non-node store) instead of NodeEfJournalStore.
        RegisterNodeDbFactory(services);
        services.AddSingleton<IJournalStore, InMemoryJournalStore>(); // WRONG for SC4-C2 (a)
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        using var provider = services.BuildServiceProvider();

        // The condition-(a) assertion the real gate makes must NOT hold here.
        Assert.IsNotType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());
    }

    // ── SC4-T9(b) Layer 2 — AP bill WRITE composition (Cohort D Step 2c) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — AP bill write DI-graph assertion (Cohort D Step 2c).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 2c adds the AP bill write + auto-posting path on the node. Bills are the primary
    /// auto-posted JE source: <c>BillPostingService.RecordAsync</c> posts the bill's balanced JE via
    /// the Step-2a node posting service. This assertion builds the REAL bill-write composition
    /// (<see cref="NodeBillWriteComposition.AddNodeBillWrites"/> over the same recoverable stores
    /// <c>Program.cs</c> wires) and confirms the SC4-C2 conditions hold for the AP write path at the
    /// resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> <c>IBillRepository</c> resolves to <see cref="NodeEfBillRepository"/> — the
    ///     recoverable AP store on <c>local-node.db</c> (the bill's only persistence sink besides the
    ///     auto-JE, which lands in the recoverable <see cref="NodeEfJournalStore"/> via the posting
    ///     service asserted by the JE Layer-2 test above).</item>
    ///   <item><b>(b)</b> <c>IDomainEventPublisher</c> is the cluster-default
    ///     <see cref="NoopDomainEventPublisher"/>, and NO non-recoverable <c>IDomainEventStore</c> is
    ///     registered — so <c>BillPostingService</c>'s event emission is a no-op (no cross-cluster bus).</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the ambient <c>ITenantContext</c> resolves to the active-team-derived
    ///     <see cref="ActiveTeamTenantContext"/> (projects the data <c>TenantId</c> from the active
    ///     team's id; ADR 0032 identity layer), never a seed-keyed per-team store.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (AP): the built bill-write DI-graph satisfies SC4-C2 — IBillRepository is NodeEfBillRepository, Noop publisher / no event-store / no event-log, tenant context is local")]
    public void BillWriteDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeBillWriteProvider();

        // (a/d) IBillRepository resolves to the recoverable node AP store.
        Assert.IsType<NodeEfBillRepository>(provider.GetRequiredService<IBillRepository>());

        // (b) The event publisher is the cluster-default Noop, and no cross-cluster event store exists.
        Assert.IsType<NoopDomainEventPublisher>(provider.GetRequiredService<IDomainEventPublisher>());
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // (d) The ambient tenant context is now the active-team-derived ActiveTeamTenantContext
        // (ADR 0032 identity layer) — it projects the data TenantId from the active team's id,
        // retiring the install-constant "local" sentinel.
        var tenantContext = provider.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>();
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>(tenantContext);
        Assert.Equal(Sc4ActiveTeamId.Value.ToString(), tenantContext.Tenant!.Id.Value);

        // Sanity: the bill posting service itself resolves over the above (proves the graph is buildable).
        Assert.IsType<BillPostingService>(provider.GetRequiredService<IBillPostingService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the AP Layer-2 assertion.</b> A deliberately MISCONFIGURED composition
    /// — <c>IBillRepository</c> pointed at the in-memory (non-node) repository instead of
    /// <see cref="NodeEfBillRepository"/> — must FAIL the condition-(a/d) assertion. Demonstrates the
    /// AP Layer-2 check is real: it passes only for a graph whose bill writes land in the recoverable
    /// node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (AP, non-vacuous): a misconfigured IBillRepository (NOT NodeEfBillRepository) FAILS condition (a/d)")]
    public void BillWriteDiGraph_MisconfiguredBillRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        // Wire the recoverable journal store (so AddNodeFinancialPosting + AddNodeBillWrites build),
        // but DELIBERATELY register IBillRepository to the in-memory (non-node) repo BEFORE
        // AddNodeBillWrites — TryAdd-free AddSingleton in the composition would still re-register, so
        // instead register the wrong one AFTER and prove the resolved type is not the node store.
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeBillWrites();
        // Override with the WRONG (non-node) bill repository — the last registration wins for the
        // service-type resolution.
        services.AddSingleton<IBillRepository, InMemoryBillRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeEfBillRepository>(provider.GetRequiredService<IBillRepository>());
    }

    // ── SC4-T9(b) Layer 2 — AR invoice WRITE composition (Cohort D Step 2b) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — AR invoice write DI-graph assertion (Cohort D Step 2b).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 2b adds the AR invoice write + posting path on the node. Issuing an invoice posts the
    /// balanced JE (Debit AR / Credit Income) via the Step-2a node posting service. This assertion
    /// builds the REAL invoice-write composition
    /// (<see cref="NodeInvoiceWriteComposition.AddNodeInvoiceWrites"/> over the same recoverable stores
    /// <c>Program.cs</c> wires) and confirms the SC4-C2 conditions hold for the AR write path at the
    /// resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> <c>IInvoiceRepository</c> resolves to <see cref="NodeEfInvoiceRepository"/> —
    ///     the recoverable AR store on <c>local-node.db</c> (the invoice's only persistence sink besides
    ///     the issue/void/write-off JEs, which land in the recoverable <see cref="NodeEfJournalStore"/>
    ///     via the posting service asserted by the JE Layer-2 test above).</item>
    ///   <item><b>(b)</b> <c>IDomainEventPublisher</c> is the cluster-default
    ///     <see cref="NoopDomainEventPublisher"/>, and NO non-recoverable <c>IDomainEventStore</c> is
    ///     registered — so <c>InvoicePostingService</c>'s event emission is a no-op (no cross-cluster bus).</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the ambient <c>ITenantContext</c> resolves to the node-resident
    ///     <see cref="StaticNodeTenantContext"/> (pins <c>TenantId("local")</c>), the numbering service
    ///     resolves to the durable node-EF <see cref="NodeEfInvoiceNumberingService"/> (derives its
    ///     sequence from the same recoverable store), never a seed-keyed per-team store.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (AR): the built invoice-write DI-graph satisfies SC4-C2 — IInvoiceRepository is NodeEfInvoiceRepository, durable node numbering, Noop publisher / no event-store / no event-log, tenant context is local")]
    public void InvoiceWriteDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeInvoiceWriteProvider();

        // (a/d) IInvoiceRepository resolves to the recoverable node AR store.
        Assert.IsType<NodeEfInvoiceRepository>(provider.GetRequiredService<IInvoiceRepository>());

        // (d) The invoice numbering service is the durable node-EF numbering (NOT the in-memory counter
        // that would re-mint a colliding -0001 after a restart) — derived state lives in the recoverable
        // store, not process memory.
        Assert.IsType<NodeEfInvoiceNumberingService>(provider.GetRequiredService<IInvoiceNumberingService>());

        // (b) The event publisher is the cluster-default Noop, and no cross-cluster event store exists.
        Assert.IsType<NoopDomainEventPublisher>(provider.GetRequiredService<IDomainEventPublisher>());
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // (d) The ambient tenant context is now the active-team-derived ActiveTeamTenantContext
        // (ADR 0032 identity layer) — it projects the data TenantId from the active team's id,
        // retiring the install-constant "local" sentinel.
        var tenantContext = provider.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>();
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>(tenantContext);
        Assert.Equal(Sc4ActiveTeamId.Value.ToString(), tenantContext.Tenant!.Id.Value);

        // Sanity: the invoice posting service itself resolves over the above (proves the graph is buildable).
        Assert.IsType<InvoicePostingService>(provider.GetRequiredService<IInvoicePostingService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the AR Layer-2 assertion.</b> A deliberately MISCONFIGURED composition
    /// — <c>IInvoiceRepository</c> pointed at the in-memory (non-node) repository instead of
    /// <see cref="NodeEfInvoiceRepository"/> — must FAIL the condition-(a/d) assertion. Demonstrates the
    /// AR Layer-2 check is real: it passes only for a graph whose invoice writes land in the recoverable
    /// node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (AR, non-vacuous): a misconfigured IInvoiceRepository (NOT NodeEfInvoiceRepository) FAILS condition (a/d)")]
    public void InvoiceWriteDiGraph_MisconfiguredInvoiceRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        // Wire the recoverable journal store + posting (so AddNodeInvoiceWrites builds).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeInvoiceWrites();
        // Override with the WRONG (non-node) invoice repository — the last registration wins for the
        // service-type resolution.
        services.AddSingleton<IInvoiceRepository, InMemoryInvoiceRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeEfInvoiceRepository>(provider.GetRequiredService<IInvoiceRepository>());
    }

    // ── SC4-T9(b) Layer 2 — node payment-WRITE composition (ADR 0122 §D4 T2; C-T2-3) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node payment-WRITE DI-graph assertion (ADR 0122 §D4 T2; the
    /// council C-T2-3 binding condition).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// T2 un-defers node payment authoring: recording an invoice/bill payment writes a Draft
    /// <c>Payment</c> + a <c>PaymentApplication</c> into the recoverable <c>local-node.db</c> and updates
    /// the target invoice/bill balance — no GL post on record (the Bridge contract records Draft;
    /// GL-posting Clear/Bounce is the deferred future lifecycle). This assertion builds the REAL
    /// payment-write composition (<see cref="NodePaymentWriteComposition.AddNodePaymentWrites"/> over the
    /// same recoverable stores <c>Program.cs</c> wires) and confirms the SC4-C2 conditions hold for the
    /// payment-write path at the resolved-service level (C-T2-3):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> <c>IPaymentRepository</c> + <c>IPaymentApplicationRepository</c> resolve to the
    ///     recoverable node payment stores (<see cref="NodeEfPaymentRepository"/> /
    ///     <see cref="NodeEfPaymentApplicationRepository"/>) on <c>local-node.db</c> — the only payment
    ///     persistence sinks (the apply path also updates the node AR/AP repos, asserted by their own
    ///     Layer-2 tests).</item>
    ///   <item><b>(b)</b> <c>IDomainEventPublisher</c> is the cluster-default
    ///     <see cref="NoopDomainEventPublisher"/>, and NO non-recoverable <c>IDomainEventStore</c> is
    ///     registered — so the apply service's event emission is a no-op.</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the ambient <c>ITenantContext</c> resolves to the active-team-derived
    ///     <see cref="ActiveTeamTenantContext"/> (projects the data <c>TenantId</c> from the active
    ///     team's id; ADR 0032 identity layer), never a seed-keyed per-team store.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Payments): the built payment-write DI-graph satisfies SC4-C2 — payment + application repos are the recoverable node stores, apply service over them, Noop publisher / no event-store / no event-log, tenant context is local")]
    public void PaymentWriteDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodePaymentWriteProvider();

        // (a/d) The payment + application repos resolve to the recoverable node stores.
        Assert.IsType<NodeEfPaymentRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentRepository>());
        Assert.IsType<NodeEfPaymentApplicationRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentApplicationRepository>());

        // (b) The event publisher is the cluster-default Noop, and no cross-cluster event store exists.
        Assert.IsType<NoopDomainEventPublisher>(provider.GetRequiredService<IDomainEventPublisher>());
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // (d) The ambient tenant context is now the active-team-derived ActiveTeamTenantContext
        // (ADR 0032 identity layer) — it projects the data TenantId from the active team's id,
        // retiring the install-constant "local" sentinel.
        var tenantContext = provider.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>();
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>(tenantContext);
        Assert.Equal(Sc4ActiveTeamId.Value.ToString(), tenantContext.Tenant!.Id.Value);

        // Sanity: the payment-application service itself resolves over the above (graph is buildable).
        Assert.IsType<Harborline.Api.Blocks.FinancialPayments.Services.DefaultPaymentApplicationService>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentApplicationService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the payment-write Layer-2 assertion.</b> A deliberately MISCONFIGURED
    /// composition — <c>IPaymentRepository</c> pointed at the in-memory (non-node) repository instead of
    /// <see cref="NodeEfPaymentRepository"/> — must FAIL the condition-(a/d) assertion. Demonstrates the
    /// payment-write Layer-2 check is real: it passes only for a graph whose payment writes land in the
    /// recoverable node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Payments, non-vacuous): a misconfigured IPaymentRepository (NOT NodeEfPaymentRepository) FAILS condition (a/d)")]
    public void PaymentWriteDiGraph_MisconfiguredPaymentRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeBillWrites();
        services.AddNodeInvoiceWrites();
        services.AddNodePaymentWrites();
        // Override with the WRONG (non-node) payment repository — the last registration wins.
        services.AddSingleton<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentRepository,
            Harborline.Api.Blocks.FinancialPayments.Services.InMemoryPaymentRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeEfPaymentRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentRepository>());
    }

    // ── SC4-T9(b) Layer 2 — node BANKING composition (T3 local-first sweep) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node banking DI-graph assertion (T3 local-first sweep).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// T3 flips the banking surface onto the node. This assertion builds the REAL banking composition
    /// (<see cref="Harborline.Api.LocalNodeHost.Data.Banking.NodeBankingWriteComposition.AddNodeBankingWrites"/>
    /// over the same recoverable stores <c>Program.cs</c> wires) and confirms the SC4-C2 conditions hold
    /// for the banking path at the resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> the four banking repos resolve to the recoverable Node EF banking stores on
    ///     <c>local-node.db</c> (<c>NodeEfBankAccountRepository</c> / <c>NodeEfStatementLineRepository</c> /
    ///     <c>NodeEfMatchLinkRepository</c> / <c>NodeEfReconciliationRepository</c>) — the only banking
    ///     persistence sinks. The fiscal-period repo resolves to the node EF
    ///     <c>NodeEfFiscalPeriodRepository</c> over the same store.</item>
    ///   <item><b>(b)</b> NO non-recoverable cross-cluster event store is registered — the banking
    ///     accept/un-match path is propose-never-auto-post (<c>AcceptMatchService</c> posts NO JE; it only
    ///     mutates MatchLink + StatementLine state in the banking repos).</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the matching engine reads the recoverable node <c>IJournalStore</c> ==
    ///     <see cref="NodeEfJournalStore"/> (read-only, for proposal heuristics), never a seed-keyed KV.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Banking): the built banking DI-graph satisfies SC4-C2 — the four banking repos + period repo are the recoverable node stores, no event-store / no event-log, matching engine reads NodeEfJournalStore")]
    public void BankingDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeBankingProvider();

        // (a/d) The four banking repos resolve to the recoverable node banking stores.
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Banking.NodeBankAccountRepositoryReader>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository>());
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Banking.NodeEfStatementLineRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IStatementLineRepository>());
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Banking.NodeEfMatchLinkRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository>());
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Banking.NodeEfReconciliationRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IReconciliationRepository>());

        // (d) The fiscal-period repo resolves to the node EF reads over local-node.db.
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Banking.NodeEfFiscalPeriodRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository>());

        // (d) The matching engine reads the recoverable node journal store (read-only heuristics).
        Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());

        // (b) No cross-cluster domain-event store is registered (banking is propose-never-auto-post).
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // Sanity: the accept-match + un-match + import services resolve over the above (graph is buildable).
        Assert.IsType<Harborline.Api.Blocks.Banking.Matching.AcceptMatchService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Matching.AcceptMatchService>());
        Assert.IsType<Harborline.Api.Blocks.Banking.Matching.UnMatchService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Matching.UnMatchService>());
        Assert.IsType<Harborline.Api.Blocks.Banking.Import.ImportPipelineService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Import.ImportPipelineService>());

        // The mock feed is the registered IBankFeedProvider (Tier-2 mock-first — no credential egress).
        Assert.IsType<Harborline.Api.Blocks.Banking.Feed.MockBankFeedProvider>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider>());

        Assert.NotNull(provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the banking Layer-2 assertion.</b> A deliberately MISCONFIGURED
    /// composition — <c>IBankAccountRepository</c> pointed at the in-memory (non-node) repository instead
    /// of <c>NodeEfBankAccountRepository</c> — must FAIL the condition-(a/d) assertion. Demonstrates the
    /// banking Layer-2 check is real: it passes only for a graph whose banking writes land in the
    /// recoverable node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Banking, non-vacuous): a misconfigured IBankAccountRepository (NOT NodeEfBankAccountRepository) FAILS condition (a/d)")]
    public void BankingDiGraph_MisconfiguredBankAccountRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeBankingWrites();
        // The feed-connection factory used by the banking route composition.
        var feedDbPath2 = Path.Combine(Path.GetTempPath(), "sc4-feed-ncfg-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddDbContextFactory<NodeLocalBankFeedDbContext>(opt =>
            opt.UseSqlite($"Data Source={feedDbPath2}",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalBankFeedDbContext.MigrationsHistoryTableName)));
        // Override with the WRONG (non-node) bank-account repository — the last registration wins.
        services.AddSingleton<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository,
            Harborline.Api.Blocks.Banking.Services.InMemoryBankAccountRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<Harborline.Api.LocalNodeHost.Data.Banking.NodeBankAccountRepositoryReader>(
            provider.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository>());
    }

    /// <summary>
    /// Builds the production node banking DI-graph: the EF context factory + the recoverable journal
    /// store + <c>AddNodeFinancialPosting</c> (so <c>IJournalStore</c> exists for
    /// <c>MatchingEngineService</c>) + <c>AddNodeBankingWrites</c> (the exact registrations
    /// <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeBankingProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // The Step-2a posting slice the matching engine reads (IJournalStore == NodeEfJournalStore).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The banking composition under test.
        services.AddNodeBankingWrites();

        // The feed-connection factory (NodeLocalBankFeedDbContext — node-exclusive, same SQLite file as
        // LocalNodeDbContext but separate context + migration history).  The Layer-2 tests only RESOLVE
        // services; they never open the DB, so a throwaway temp-file path suffices.
        var feedDbPath = Path.Combine(Path.GetTempPath(), "sc4-feed-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddDbContextFactory<NodeLocalBankFeedDbContext>(opt =>
            opt.UseSqlite($"Data Source={feedDbPath}",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalBankFeedDbContext.MigrationsHistoryTableName)));

        return services.BuildServiceProvider();
    }

    // ── SC4-T9(b) Layer 2 — node PAYROLL composition (T4 local-first sweep) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node payroll DI-graph assertion (T4 local-first sweep).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// T4 flips the payroll surface onto the node. This assertion builds the REAL payroll composition
    /// (<see cref="NodePayrollWriteComposition.AddNodePayrollWrites"/> over the recoverable stores
    /// <c>Program.cs</c> wires) and confirms the SC4-C2 conditions hold for the payroll path at the
    /// resolved-service level:
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> the three payroll repos resolve to the recoverable Node EF payroll stores
    ///     (<see cref="NodeEfEmployeeRepository"/> / <see cref="NodeEfPayRunRepository"/> /
    ///     <see cref="NodeEfFilingObligationRepository"/>) over the node-exclusive
    ///     <see cref="NodeLocalPayrollDbContext"/> on <c>local-node.db</c> — the only payroll persistence
    ///     sinks. The pay-run JE posts through the recoverable <c>NodeEfJournalStore</c> (the node posting
    ///     composition's <c>IJournalStore</c>), never a seed-keyed KV.</item>
    ///   <item><b>(b)</b> NO non-recoverable cross-cluster <c>IDomainEventStore</c> is registered — the
    ///     pay-run post path posts a balanced JournalEntry through the GL-of-record posting service; it
    ///     does not touch the event store directly.</item>
    ///   <item><b>(a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope.</item>
    ///   <item><b>(d)</b> the ambient <c>ITenantContext</c> resolves to the active-team-derived
    ///     <see cref="ActiveTeamTenantContext"/> (projects the data <c>TenantId</c> from the active
    ///     team's id; ADR 0032 identity layer), never a seed-keyed per-team store.</item>
    /// </list>
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Payroll): the built payroll DI-graph satisfies SC4-C2 — the three payroll repos are the recoverable node stores, posting service over the node IJournalStore, no event-store / no event-log, tenant context is local")]
    public void PayrollDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodePayrollProvider();

        // (a/d) The three payroll repos resolve to the recoverable node payroll stores.
        Assert.IsType<NodeEfEmployeeRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IEmployeeRepository>());
        Assert.IsType<NodeEfPayRunRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IPayRunRepository>());
        Assert.IsType<NodeEfFilingObligationRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IFilingObligationRepository>());

        // (d) The pay-run posting service posts through the recoverable node journal store.
        Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());

        // (b) No cross-cluster domain-event store is registered (the pay-run post path posts a JE
        // through the GL-of-record posting service, not the event store directly).
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // (d) The ambient tenant context is now the active-team-derived ActiveTeamTenantContext
        // (ADR 0032 identity layer) — it projects the data TenantId from the active team's id,
        // retiring the install-constant "local" sentinel.
        var tenantContext = provider.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>();
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>(tenantContext);
        Assert.Equal(Sc4ActiveTeamId.Value.ToString(), tenantContext.Tenant!.Id.Value);

        // Sanity: the pay-run posting service resolves over the above (graph buildable).
        Assert.IsType<Harborline.Api.Blocks.Payroll.Services.PayRunPostingService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IPayRunPostingService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the payroll Layer-2 assertion.</b> A deliberately MISCONFIGURED
    /// composition — <c>IPayRunRepository</c> pointed at the in-memory (non-node) repository instead of
    /// <see cref="NodeEfPayRunRepository"/> — must FAIL the condition-(a/d) assertion. Demonstrates the
    /// payroll Layer-2 check is real: it passes only for a graph whose pay-run writes land in the
    /// recoverable node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Payroll, non-vacuous): a misconfigured IPayRunRepository (NOT NodeEfPayRunRepository) FAILS condition (a/d)")]
    public void PayrollDiGraph_MisconfiguredPayRunRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        RegisterNodePayrollDbFactory(services);
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeBillWrites();
        services.AddNodePayrollWrites();
        // Override with the WRONG (non-node) pay-run repository — the last registration wins.
        services.AddSingleton<Harborline.Api.Blocks.Payroll.Services.IPayRunRepository,
            Harborline.Api.Blocks.Payroll.Services.InMemoryPayRunRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeEfPayRunRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IPayRunRepository>());
    }

    // ── SC4-T9(b) Layer 2 — node DOCUMENTS write+read composition (ADR 0127 T4; SEC-1 binding gate) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node documents DI-graph assertion (ADR 0127 T4; SEC-1 binding).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR 0127 flips the documents surface onto the node. Document bytes live INLINE in the
    /// recoverable SQLCipher <c>local-node.db</c> (<c>StorageRef.ForInline</c> → base64 inside
    /// <c>storage_ref_json</c>), so SC-1/SC-4 are satisfied by construction. This assertion builds the
    /// REAL docs composition (<see cref="NodeDocsWriteComposition.AddNodeDocsWrites"/> — the SAME method
    /// <c>Program.cs</c> calls) and confirms the SC4-C2 conditions hold for the documents path at the
    /// resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a/d)</b> <c>IAttachmentRepository</c> resolves to <see cref="NodeEfAttachmentRepository"/>
    ///     and <c>IDocumentRefRepository</c> to <see cref="NodeEfDocumentRefRepository"/> — the recoverable
    ///     docs stores on <c>local-node.db</c> (the documents path's ONLY persistence sinks; inline bytes
    ///     ride inside the SQLCipher <c>storage_ref_json</c> column).</item>
    ///   <item><b>SEC-1</b> <c>IMimeTypeAndSizePolicy</c> resolves to the CONCRETE
    ///     <see cref="NodeInlineCeilingPolicy"/> (enforcing the 25&#160;MB inline ceiling over the shared
    ///     three-gate policy), and <c>IAttachmentService</c> to the concrete
    ///     <see cref="AttachmentService"/> — built WITH that non-null policy, so the enforced-ceiling
    ///     guarantee cannot evaporate.</item>
    ///   <item><b>(b)</b> NO <c>IDomainEventStore</c> is registered by the docs composition.</item>
    ///   <item><b>(c/a-runtime)</b> NO per-team kernel <c>IEventLog</c> is reachable at install scope —
    ///     the docs repos + services take NO <c>IEventLog</c> dependency (documents post no JE / touch no
    ///     cross-cluster bus). Keeps the orphan vector closed.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Docs): the built documents DI-graph satisfies SC4-C2 — attachment + document-ref repos are the recoverable node stores, CONCRETE inline-ceiling policy + AttachmentService (SEC-1), no event-store / no event-log")]
    public void DocsDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeDocsProvider();

        // (a/d) The docs repos resolve to the recoverable node stores on local-node.db.
        Assert.IsType<NodeEfAttachmentRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentRepository>());
        Assert.IsType<NodeEfDocumentRefRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IDocumentRefRepository>());

        // SEC-1 (build-blocking): the policy is the CONCRETE node inline-ceiling policy, and the
        // upload service is the concrete AttachmentService built WITH it (non-null). The enforced
        // 25 MB ceiling cannot silently evaporate.
        Assert.IsType<NodeInlineCeilingPolicy>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IMimeTypeAndSizePolicy>());
        Assert.IsType<Harborline.Api.Blocks.Docs.Services.AttachmentService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentService>());

        // (b) No cross-cluster domain-event store is registered by the docs composition.
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (c/a-runtime) No per-team kernel event log is reachable at install scope.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // Sanity: the link service resolves over the above (graph buildable).
        Assert.IsType<Harborline.Api.Blocks.Docs.Services.DocumentRefService>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IDocumentRefService>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the docs Layer-2 assertion.</b> A deliberately MISCONFIGURED composition
    /// — <c>IAttachmentRepository</c> pointed at the in-memory (non-node) repository instead of
    /// <see cref="NodeEfAttachmentRepository"/> — must FAIL the condition-(a/d) assertion. Demonstrates
    /// the docs Layer-2 check is real: it passes only for a graph whose document writes land in the
    /// recoverable node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Docs, non-vacuous): a misconfigured IAttachmentRepository (NOT NodeEfAttachmentRepository) FAILS condition (a/d)")]
    public void DocsDiGraph_MisconfiguredAttachmentRepository_FailsCondition()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddNodeDocsWrites();
        // Override with the WRONG (non-node) attachment repository — the last registration wins.
        services.AddSingleton<Harborline.Api.Blocks.Docs.Services.IAttachmentRepository,
            Harborline.Api.Blocks.Docs.Services.InMemoryAttachmentRepository>(); // WRONG for SC4-C2 (a/d)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeEfAttachmentRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentRepository>());
    }

    /// <summary>
    /// <b>SEC-1 fail-closed proof (ADR 0127 build-blocking).</b> The node inline-ceiling policy MUST
    /// reject a misconfigured (non-positive) ceiling at construction — a zero/negative ceiling would
    /// disable the enforced inline gate, the exact "enforced ceiling evaporates" vector the SEC-1
    /// criterion forbids. Proves the fail-closed guard is real.
    /// </summary>
    [Fact(DisplayName = "SEC-1 (ADR 0127): NodeInlineCeilingPolicy with a non-positive ceiling throws (fail-closed; the enforced ceiling cannot be disabled)")]
    public void NodeInlineCeilingPolicy_NonPositiveCeiling_ThrowsFailClosed()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddNodeDocsWrites();
        var repo = services.BuildServiceProvider()
            .GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentRepository>();
        var shared = new Harborline.Api.Blocks.Docs.Services.MimeTypeAndSizePolicy(
            new Harborline.Api.Blocks.Docs.Models.BlocksDocsOptions(), repo);

        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeInlineCeilingPolicy(shared, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeInlineCeilingPolicy(shared, -1));
    }

    /// <summary>
    /// <b>Inline ceiling enforcement proof (ADR 0127 = 25&#160;MB).</b> The node inline-ceiling policy
    /// ACCEPTS a payload at the 25&#160;MB ceiling and REJECTS one above it with the dedicated
    /// <see cref="Harborline.Api.Blocks.Docs.Services.PolicyRejection.InlineSize"/> reason — proving the
    /// enforced ceiling (via <c>InlineBlobMaxBytes</c>, not <c>MaxAttachmentBytes</c>) is wired, not
    /// dormant. A whitelisted MIME (PNG magic bytes) is used so the shared gates pass.
    /// </summary>
    [Fact(DisplayName = "Inline ceiling (ADR 0127): NodeInlineCeilingPolicy accepts at 25 MB and rejects above with PolicyRejection.InlineSize")]
    public async Task NodeInlineCeilingPolicy_EnforcesTwentyFiveMbCeiling()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddNodeDocsWrites();
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IMimeTypeAndSizePolicy>();
        var tenant = new Harborline.Api.Foundation.Assets.Common.TenantId(StaticNodeTenantContext.LocalTenantId);

        const long ceiling = NodeDocsWriteComposition.InlineCeilingBytes; // 25 MB
        // image/png is in the default whitelist; size is the dimension under test.
        var atCeiling = await policy.ValidateAsync(tenant, "image/png", ceiling);
        Assert.False(atCeiling.Rejected);

        var aboveCeiling = await policy.ValidateAsync(tenant, "image/png", ceiling + 1);
        Assert.True(aboveCeiling.Rejected);
        Assert.Equal(Harborline.Api.Blocks.Docs.Services.PolicyRejection.InlineSize, aboveCeiling.RejectionReason);
    }

    // ── SC4-T9(b) Layer 2 — node AUDIT write+read composition (ADR 0126 T4; SE-1 binding gate) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node audit DI-graph assertion (ADR 0126 T4; SE-1 binding).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR 0126 flips the node audit system-of-record off the seed-keyed kernel <c>IEventLog</c> onto an
    /// append-only table in the recoverable <c>local-node.db</c>. This assertion builds the REAL audit
    /// composition (<see cref="NodeAuditComposition.AddNodeAuditWrites"/> +
    /// <see cref="NodeAuditComposition.AddNodeAuditReads"/> — the SAME methods <c>Program.cs</c> calls)
    /// and confirms the SC4-C2 conditions hold for the audit path at the resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a)</b> the audit WRITE enlister (<see cref="INodeAuditWriteEnlister"/>) resolves to
    ///     <see cref="NodeAuditWriteEnlister"/>, which stages onto the SAME recoverable
    ///     <see cref="LocalNodeDbContext"/> the JE write commits — the audit row's only sink is
    ///     <c>local-node.db</c>. <c>IJournalStore</c> still resolves to <see cref="NodeEfJournalStore"/>
    ///     (the JE + audit row commit in one recoverable transaction).</item>
    ///   <item><b>(a/d)</b> the audit READ-model (<see cref="NodeAuditEventReader"/>) resolves over the
    ///     node EF context factory — it reads <c>local-node.db</c>, never a seed-keyed per-team KV.</item>
    ///   <item><b>(b)</b> NO <c>IDomainEventStore</c> is registered by the audit composition.</item>
    ///   <item><b>(c/a-runtime)</b> NO per-team kernel <c>IEventLog</c> is reachable at install scope —
    ///     the audit enlister + reader take NO <c>IEventLog</c> dependency. This is the property that
    ///     keeps the orphan vector closed: a future edit injecting <c>IEventLog</c> into the audit path
    ///     would fail this assertion (and the Layer-1 forbidden-set scan).</item>
    /// </list>
    /// <para>
    /// <b>Non-vacuous:</b> the composition is the production one; the assertions are POSITIVE (the
    /// enlister/reader ARE the recoverable node types) plus NEGATIVE (no IDomainEventStore / IEventLog),
    /// paired with the misconfiguration counter-test below.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Audit): the built audit DI-graph satisfies SC4-C2 — enlister is NodeAuditWriteEnlister staging onto local-node.db, reader is NodeAuditEventReader, IJournalStore is NodeEfJournalStore, no event-store / no event-log")]
    public void AuditDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeAuditProvider();

        // (a) The atomic-audit enlister resolves to the recoverable node enlister; the JE store is
        // still the recoverable NodeEfJournalStore (the audit row joins ITS transaction).
        Assert.IsType<NodeAuditWriteEnlister>(provider.GetRequiredService<INodeAuditWriteEnlister>());
        Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());

        // (a/d) The audit read-model resolves over the node EF factory (reads local-node.db).
        Assert.IsType<NodeAuditEventReader>(provider.GetRequiredService<NodeAuditEventReader>());

        // (b) No cross-cluster domain-event store is registered by the audit composition.
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (c / a-runtime) No per-team kernel event log is reachable at install scope — the audit
        // enlister + reader are pure compute + recoverable EF, taking NO IEventLog dependency.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the audit Layer-2 assertion.</b> A deliberately MISCONFIGURED
    /// composition — <c>INodeAuditWriteEnlister</c> overridden with an alternate (non-default)
    /// implementation — must FAIL the condition-(a) assertion. Demonstrates the audit Layer-2 check is
    /// real: it passes only for a graph whose audit enlister IS the recoverable
    /// <see cref="NodeAuditWriteEnlister"/> staging onto <c>local-node.db</c>.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Audit, non-vacuous): a misconfigured INodeAuditWriteEnlister (NOT NodeAuditWriteEnlister) FAILS condition (a)")]
    public void AuditDiGraph_MisconfiguredEnlister_FailsConditionA()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeAuditWrites();
        services.AddNodeAuditReads();
        // Override with a WRONG (non-default) enlister — the last registration wins. A no-op enlister
        // that does NOT stage onto local-node.db would silently drop the atomic audit row.
        services.AddSingleton<INodeAuditWriteEnlister, NoopAuditWriteEnlister>(); // WRONG for SC4-C2 (a)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<NodeAuditWriteEnlister>(provider.GetRequiredService<INodeAuditWriteEnlister>());
    }

    /// <summary>A deliberately-wrong audit enlister used ONLY by the non-vacuity counter-test: it stages
    /// nothing onto the recoverable store, so a graph wiring it does NOT satisfy SC4-C2 (a).</summary>
    private sealed class NoopAuditWriteEnlister : INodeAuditWriteEnlister
    {
        public Task EnlistJournalPostedAsync(
            LocalNodeDbContext ctx,
            Harborline.Api.Blocks.FinancialLedger.Models.JournalEntry entry,
            Harborline.Api.Foundation.Authorization.AuthorizationDecision decision,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── SC4-T9(b) Layer 2 — node WORKFLOW engine composition (ADR 0135 slice 2) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node durable-process-engine DI-graph assertion (ADR 0135 slice 2).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Slice 2 wires the durable process engine (<c>AddNodeWorkflowEngine</c> + <c>AddNodeWorkflowHandlers</c>
    /// — the SAME methods <c>Program.cs</c> calls). The engine's only persistence sink is the recoverable
    /// <c>local-node.db</c> (the workflow store rides <see cref="LocalNodeDbContext"/> via
    /// <see cref="Harborline.Api.LocalNodeHost.Data.Workflow.WorkflowEntityModule"/>; the step effects stage a
    /// <c>JournalEntry</c> onto the SAME context). This assertion confirms the SC4-C2 conditions hold at the
    /// resolved-service level:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>(a)</b> <c>IWorkflowStore</c> resolves to
    ///     <see cref="Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore"/> (recoverable
    ///     <c>local-node.db</c>) — the engine's only durable sink.</item>
    ///   <item><b>(b)</b> NO non-recoverable cross-cluster <c>IDomainEventStore</c> is registered by the
    ///     workflow composition.</item>
    ///   <item><b>(c / a-runtime)</b> NO per-team kernel <c>IEventLog</c> is reachable at install scope — the
    ///     engine + handlers take NO <c>IEventLog</c> dependency (blocks-workflow references no kernel CRDT
    ///     writer, so the Layer-1 forbidden-set IL scan is also unaffected — the orphan vector stays closed).</item>
    ///   <item><b>buildability</b> the dispatcher + both v1 handlers
    ///     (<c>invoice-approval</c>, <c>recurring-generation</c>) + the production schedule source resolve
    ///     over the recoverable store — proving the engine is live + node-resident, not absent.</item>
    /// </list>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Workflow): the built process-engine DI-graph satisfies SC4-C2 — IWorkflowStore is NodeEfWorkflowStore, dispatcher + both handlers + schedule source resolve, no event-store / no event-log")]
    public void WorkflowEngineDiGraph_SatisfiesSc4C2Conditions()
    {
        using var provider = BuildNodeWorkflowProvider();

        // (a) IWorkflowStore resolves to the recoverable node store on local-node.db.
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore>(
            provider.GetRequiredService<Harborline.Api.Blocks.Workflow.Durable.IWorkflowStore>());

        // (b) No cross-cluster domain-event store is registered by the workflow composition.
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (c / a-runtime) No per-team kernel event log is reachable at install scope — the engine + handlers
        // take NO IEventLog dependency.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // buildability: the dispatcher resolves over the recoverable store, and BOTH v1 handlers are wired
        // (invoice-approval + recurring-generation), plus the production schedule source.
        Assert.IsType<Harborline.Api.Blocks.Workflow.Durable.WorkflowTriggerDispatcher>(
            provider.GetRequiredService<Harborline.Api.Blocks.Workflow.Durable.IWorkflowTriggerDispatcher>());
        var handlerKeys = provider.GetServices<Harborline.Api.Blocks.Workflow.Durable.IWorkflowStepHandler>()
            .Select(h => h.DefinitionKey).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Harborline.Api.Blocks.Workflow.Durable.InvoiceApprovalSteps.DefinitionKey, handlerKeys);
        Assert.Contains(Harborline.Api.Blocks.Workflow.Durable.RecurringGenerationSteps.DefinitionKey, handlerKeys);
        Assert.IsType<Harborline.Api.LocalNodeHost.Data.Workflow.NodeRecurringScheduleSource>(
            provider.GetRequiredService<Harborline.Api.Blocks.Workflow.Durable.IWorkflowScheduleSource>());
    }

    /// <summary>
    /// <b>Non-vacuity proof for the workflow Layer-2 assertion.</b> A deliberately MISCONFIGURED composition
    /// — <c>IWorkflowStore</c> pointed at the in-memory (non-node) store instead of
    /// <see cref="Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore"/> — must FAIL the condition-(a)
    /// assertion. Demonstrates the workflow Layer-2 check is real: it passes only for a graph whose workflow
    /// state lands in the recoverable node store.
    /// </summary>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Workflow, non-vacuous): a misconfigured IWorkflowStore (NOT NodeEfWorkflowStore) FAILS condition (a)")]
    public void WorkflowEngineDiGraph_MisconfiguredStore_FailsConditionA()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();
        services.AddNodeWorkflowEngine();
        services.AddNodeWorkflowHandlers();
        // Override with the WRONG (non-node) workflow store — the last registration wins.
        services.AddSingleton<Harborline.Api.Blocks.Workflow.Durable.IWorkflowStore,
            FakeNonNodeWorkflowStore>(); // WRONG for SC4-C2 (a)
        using var provider = services.BuildServiceProvider();

        Assert.IsNotType<Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore>(
            provider.GetRequiredService<Harborline.Api.Blocks.Workflow.Durable.IWorkflowStore>());
    }

    /// <summary>A non-recoverable (in-memory) workflow store used ONLY by the non-vacuity counter-test.</summary>
    private sealed class FakeNonNodeWorkflowStore : Harborline.Api.Blocks.Workflow.Durable.IWorkflowStore
    {
        public Task<Harborline.Api.Blocks.Workflow.Durable.WorkflowInstanceRecord?> LoadAsync(string instanceId, CancellationToken ct = default)
            => Task.FromResult<Harborline.Api.Blocks.Workflow.Durable.WorkflowInstanceRecord?>(null);
        public Task CreateInstanceAsync(Harborline.Api.Blocks.Workflow.Durable.WorkflowInstanceRecord instance, DateTimeOffset at, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<Harborline.Api.Blocks.Workflow.Durable.WorkflowStepIdempotencyRecord?> FindStepResultAsync(
            Harborline.Api.Blocks.Workflow.Durable.WorkflowStepKey key, CancellationToken ct = default)
            => Task.FromResult<Harborline.Api.Blocks.Workflow.Durable.WorkflowStepIdempotencyRecord?>(null);
        public Task AdvanceAsync(
            Harborline.Api.Blocks.Workflow.Durable.WorkflowStepKey key,
            Harborline.Api.Blocks.Workflow.Durable.WorkflowEffect? effect,
            string resultJson, string eventType, string eventDataJson, string nextStep,
            Harborline.Api.Blocks.Workflow.Durable.WorkflowStatus nextStatus,
            DateTimeOffset at, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ParkAsync(
            string instanceId, string step, string reasonJson, DateTimeOffset at, int iteration = 0,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Builds the production node workflow DI-graph: the EF context factory + the recoverable journal store +
    /// <c>AddNodeFinancialPosting</c> (so <c>TimeProvider</c> — the engine clock — is registered, exactly as
    /// <c>Program.cs</c> does) + <c>AddNodeWorkflowEngine</c> + <c>AddNodeWorkflowHandlers</c> (the exact
    /// registrations <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeWorkflowProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddTestKernelClock();

        // The posting slice supplies TimeProvider (the engine + handler clock) the same way Program.cs does.
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The workflow composition under test (ADR 0135 slice 2).
        services.AddTestAuthorizationGate();
        services.AddNodeWorkflowEngine();
        services.AddNodeWorkflowHandlers();

        return services.BuildServiceProvider();
    }

    // ── SC4-T9(b) Layer 2 — node REPORTS composition (T5 local-first sweep) ──

    /// <summary>
    /// <b>SC4-T9(b) Layer 2 extension — node reports DI-graph assertion (T5 local-first sweep).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// T5 flips the read-side report cartridge family onto the node. Reports are READ-ONLY — unlike the
    /// AR/AP/banking/payroll write slices there is NO write path to guard. This assertion builds the REAL
    /// reports read composition (the exact registrations <c>Program.cs</c> makes — the two new node read
    /// seams + the cartridge substrate over the node read deps) and confirms the SC4-C2 read-only posture
    /// holds at the resolved-service level:
    /// <list type="bullet">
    ///   <item><b>(d)</b> <c>IChartRepository</c> resolves to the new node read seam
    ///     <see cref="NodeEfChartRepository"/> (pure EF read over <c>local-node.db</c>), and
    ///     <c>IGeneralLedgerReadModel</c> resolves to <see cref="InMemoryGeneralLedgerReadModel"/> over the
    ///     recoverable <see cref="NodeEfJournalStore"/> — both pure read projections, never a write store.</item>
    ///   <item><b>(b)</b> NO non-recoverable cross-cluster <c>IDomainEventStore</c> is registered — reports
    ///     aggregate; they emit nothing.</item>
    ///   <item><b>(c / a-runtime)</b> no per-team kernel <c>IEventLog</c> is reachable at install scope —
    ///     the runner + cartridges + read seams are pure compute + recoverable EF, taking NO IEventLog
    ///     dependency (the Layer-1 IL scan additionally proves the host names no PostingEngine /
    ///     ILedgerEventStream / FileBackedEventLog / IEventLog at all).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Buildability = the gate.</b> A read-only slice has no write store to point at the wrong place, so
    /// the strongest assertion is that the FULL report run graph RESOLVES: <c>IReportRunner</c> + all six
    /// cartridges the pages use compose over the node read deps. A drift that broke a cartridge's read-dep
    /// wiring (e.g. removed the node <c>IGeneralLedgerReadModel</c> registration) would fail this
    /// immediately — proving the read substrate is live and node-resident, not absent.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "SC4-T9(b) Layer 2 (Reports): the built reports read DI-graph is READ-ONLY over local-node.db — IChartRepository is NodeEfChartRepository, IGeneralLedgerReadModel is InMemoryGeneralLedgerReadModel over NodeEfJournalStore, the runner + all 6 cartridges resolve, no event-store / no event-log")]
    public void ReportsDiGraph_IsReadOnlyOverLocalNodeDb()
    {
        using var provider = BuildNodeReportsProvider();

        // (d) The two new node read seams resolve to the pure-read node types over local-node.db.
        Assert.IsType<NodeEfChartRepository>(
            provider.GetRequiredService<Harborline.Api.Blocks.FinancialPeriods.Services.IChartRepository>());
        Assert.IsType<InMemoryGeneralLedgerReadModel>(
            provider.GetRequiredService<IGeneralLedgerReadModel>());
        // The GL read-model reads the recoverable NodeEfJournalStore (no separate write store).
        Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());

        // (b) No cross-cluster domain-event store is registered — reports emit nothing.
        Assert.Null(provider.GetService<IDomainEventStore>());

        // (c / a-runtime) No per-team kernel event log is reachable — reports are pure read compute.
        Assert.Null(provider.GetService<Harborline.Api.Kernel.Events.IEventLog>());

        // Buildability: the runner + all six cartridges the pages use resolve over the node read deps,
        // and the registry drains cleanly (UseBlocksReports returns 6 registrars).
        Assert.NotNull(provider.GetRequiredService<Harborline.Api.Blocks.Reports.IReportRunner>());
        var drained = provider.UseBlocksReports();
        Assert.Equal(6, drained);
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.TrialBalance.TrialBalanceCartridge>());
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.ArAgingSummary.ArAgingSummaryCartridge>());
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary.ApAgingSummaryCartridge>());
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.BalanceSheet.BalanceSheetCartridge>());
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss.ProfitAndLossCartridge>());
        Assert.NotNull(provider.GetRequiredService<
            Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty.ProfitAndLossByPropertyCartridge>());
    }

    /// <summary>
    /// Builds the production node reports read DI-graph: the EF context factory + the recoverable journal
    /// store + all the node read deps the cartridges resolve (posting resolvers, AR/AP/banking/contacts
    /// compositions) + the two new node read seams + the cartridge substrate and the six cartridges — the
    /// exact registrations <c>Program.cs</c> makes. No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeReportsProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // The recoverable journal store + posting slice (IJournalStore == NodeEfJournalStore; the
        // IAccountResolver the cartridges read).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The AR/AP write slices (IInvoiceRepository / IBillRepository the aging services read) + the
        // banking slice (IFiscalPeriodRepository the Trial Balance / Balance Sheet cartridges read) +
        // contacts (IPartyReadModel the AR/AP aging cartridges read).
        services.AddNodeInvoiceWrites();
        services.AddNodeBillWrites();
        services.AddNodeBankingWrites();
        services.AddNodeContacts();

        // The feed-connection factory AddNodeBankingWrites' resolution closure needs (node-exclusive).
        var feedDbPath = Path.Combine(Path.GetTempPath(), "sc4-reports-feed-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddDbContextFactory<NodeLocalBankFeedDbContext>(opt =>
            opt.UseSqlite($"Data Source={feedDbPath}",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalBankFeedDbContext.MigrationsHistoryTableName)));

        // The two genuinely-new node read seams (the T5 substrate).
        services.AddSingleton<Harborline.Api.Blocks.FinancialPeriods.Services.IChartRepository, NodeEfChartRepository>();
        services.AddSingleton<IGeneralLedgerReadModel>(sp =>
            new InMemoryGeneralLedgerReadModel(sp.GetRequiredService<IJournalStore>()));

        // The AR/AP aging services (narrowed MultiTenancy.ITenantContext consumer variant).
        services.AddSingleton<Harborline.Api.Blocks.FinancialAr.Services.IArAgingService>(sp =>
            new Harborline.Api.Blocks.FinancialAr.Services.ArAgingService(
                sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialAr.Services.IInvoiceRepository>()));
        services.AddSingleton<Harborline.Api.Blocks.FinancialAp.Services.IApAgingService>(sp =>
            new Harborline.Api.Blocks.FinancialAp.Services.ApAgingService(
                sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialAp.Services.IBillRepository>()));

        // The report cartridge substrate + the six cartridges the pages use (Program.cs registration set).
        services.AddBlocksReportsSubstrate();
        services.AddTrialBalanceCartridge();
        services.AddArAgingSummaryCartridge();
        services.AddApAgingSummaryCartridge();
        services.AddBalanceSheetCartridge();
        services.AddProfitAndLossCartridge();
        services.AddProfitAndLossByPropertyCartridge();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node audit DI-graph: the EF context factory + the recoverable journal store
    /// + <c>AddNodeFinancialPosting</c> (so <c>TimeProvider</c> — the audit clock — is registered, exactly
    /// as <c>Program.cs</c> does) + <c>AddNodeAuditWrites</c> + <c>AddNodeAuditReads</c> (the exact
    /// registrations <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeAuditProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // The posting slice the audit-write atomicity rides on (IJournalStore + TimeProvider).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The audit composition under test (ADR 0126 T4).
        services.AddNodeAuditWrites();
        services.AddNodeAuditReads();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node payroll DI-graph: the EF context factories (financial +
    /// payroll-exclusive) + the recoverable journal store + <c>AddNodeFinancialPosting</c> +
    /// <c>AddNodeBillWrites</c> (so <c>IJournalPostingService</c> + the node <c>ITenantContext</c> exist
    /// for <c>PayRunPostingService</c>) + <c>AddNodePayrollWrites</c> (the exact registrations
    /// <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodePayrollProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        RegisterNodePayrollDbFactory(services);

        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // AddNodeBillWrites registers the active-team-derived ActiveTeamTenantContext (ITenantContext,
        // ADR 0032) the pay-run posting service reads (AddNodePayrollWrites TryAdds the same impl type, so
        // this is the registration; mirrors Program.cs which runs bill writes before payroll writes).
        services.AddNodeBillWrites();

        // The payroll composition under test.
        services.AddNodePayrollWrites();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Registers the node-exclusive payroll EF context factory (<see cref="NodeLocalPayrollDbContext"/>).
    /// The Layer-2 tests only RESOLVE services, never open the DB, so a throwaway temp-file path suffices.
    /// </summary>
    private static void RegisterNodePayrollDbFactory(IServiceCollection services)
    {
        var payrollDbPath = Path.Combine(Path.GetTempPath(), "sc4-payroll-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddDbContextFactory<NodeLocalPayrollDbContext>(opt =>
            opt.UseSqlite($"Data Source={payrollDbPath}",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalPayrollDbContext.MigrationsHistoryTableName)));
    }

    /// <summary>
    /// Builds the production node payment-write DI-graph: the EF context factory + the recoverable
    /// journal store + <c>AddNodeFinancialPosting</c> + <c>AddNodeBillWrites</c> + <c>AddNodeInvoiceWrites</c>
    /// (so the apply service's AR/AP repos + tenant context exist) + <c>AddNodePaymentWrites</c> (the exact
    /// registrations <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodePaymentWriteProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The apply service composes over the node AR/AP repos + the node-resident tenant context.
        services.AddNodeBillWrites();
        services.AddNodeInvoiceWrites();

        // The payment-write composition under test.
        services.AddNodePaymentWrites();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node invoice-write DI-graph: the EF context factory + the recoverable
    /// journal store + <c>AddNodeFinancialPosting</c> (so <c>IJournalPostingService</c> exists for
    /// <c>InvoicePostingService</c>) + <c>AddNodeInvoiceWrites</c> (the exact registrations
    /// <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeInvoiceWriteProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // The Step-2a posting slice the invoice writes depend on (IJournalPostingService + IJournalStore).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The invoice-write composition under test.
        services.AddNodeInvoiceWrites();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node bill-write DI-graph: the EF context factory + the recoverable
    /// journal store + <c>AddNodeFinancialPosting</c> (so <c>IJournalPostingService</c> exists for
    /// <c>BillPostingService</c>) + <c>AddNodeBillWrites</c> (the exact registrations
    /// <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeBillWriteProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // The Step-2a posting slice the bill writes depend on (IJournalPostingService).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        // The bill-write composition under test.
        services.AddNodeBillWrites();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node documents DI-graph: the EF context factory + the root clock +
    /// <c>AddNodeDocsWrites</c> (the exact registrations <c>Program.cs</c> makes). Documents post no
    /// JE, so — unlike the financial slices — no journal store / posting composition is needed. No
    /// <c>AddFoundationEvents()</c>, no kernel registrar.
    /// </summary>
    private static ServiceProvider BuildNodeDocsProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);
        services.AddTestKernelClock();

        // The documents composition under test (ADR 0127 T4).
        services.AddNodeDocsWrites();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production node posting DI-graph: the EF context factory + the recoverable
    /// <see cref="NodeEfJournalStore"/> as <c>IJournalStore</c> + <c>AddNodeFinancialPosting</c> (the
    /// exact registrations <c>Program.cs</c> makes). No <c>AddFoundationEvents()</c>, no kernel
    /// registrar — mirroring the host's posting slice so the Layer-2 assertion verifies the real
    /// composition with no drift.
    /// </summary>
    private static ServiceProvider BuildNodePostingProvider()
    {
        var services = new ServiceCollection();
        RegisterNodeDbFactory(services);

        // IJournalStore == the recoverable NodeEfJournalStore (Program.cs registration shape).
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());

        // The posting composition under test.
        services.AddTestAuthorizationGate();
        services.AddNodeFinancialPosting();
        services.AddTestAuthorizationGate();

        return services.BuildServiceProvider();
    }

    /// <summary>Registers an in-memory-file SQLite-backed LocalNodeDbContext factory for the DI-graph tests.</summary>
    private static void RegisterNodeDbFactory(IServiceCollection services)
    {
        // A throwaway temp-file SQLite store — the Layer-2 tests only RESOLVE services, they never
        // open the DB, so the connection string just needs to be valid for the factory to build.
        var dbPath = Path.Combine(Path.GetTempPath(), "sc4-di-graph-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

        // ADR 0032 identity layer: the production ambient ITenantContext is now
        // ActiveTeamTenantContext (active-team-derived). The Layer-2 graphs register it via
        // AddNodeBillWrites / AddNodeInvoiceWrites, so the graph needs an IActiveTeamAccessor with a
        // materialized active team for the tenant context to resolve.
        var team = new Harborline.Api.Kernel.Runtime.Teams.TeamContext(
            Sc4ActiveTeamId, "SC4 Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        services.AddSingleton<Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor>(
            new Sc4FakeActiveTeamAccessor(team));
    }

    /// <summary>The fixed team the SC4 Layer-2 graphs activate (its id projects to the ambient tenant).</summary>
    private static readonly Harborline.Api.Kernel.Runtime.Teams.TeamId Sc4ActiveTeamId =
        new(Guid.Parse("5c400000-0000-0000-0000-0000000000a1"));

    /// <summary>Minimal active-team accessor with a fixed materialized team for the DI-graph tests.</summary>
    private sealed class Sc4FakeActiveTeamAccessor : Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor
    {
        public Sc4FakeActiveTeamAccessor(Harborline.Api.Kernel.Runtime.Teams.TeamContext? active) => Active = active;
        public Harborline.Api.Kernel.Runtime.Teams.TeamContext? Active { get; }
        public Task SetActiveAsync(Harborline.Api.Kernel.Runtime.Teams.TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<Harborline.Api.Kernel.Runtime.Teams.ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(
            this, new Harborline.Api.Kernel.Runtime.Teams.ActiveTeamChangedEventArgs(null, null));
    }

    /// <summary>
    /// Reads the host assembly's metadata <c>TypeReference</c> table and returns the
    /// distinct set of (<c>Namespace</c>, <c>Name</c>) pairs it references. Uses
    /// <see cref="System.Reflection.Metadata"/> so it sees every type the host's IL
    /// actually touches (including transitively-available types used in the
    /// composition root), not just the direct assembly-reference list.
    /// </summary>
    private static HashSet<(string Ns, string Name)> ReferencedTypeNames(Assembly assembly)
    {
        var result = new HashSet<(string, string)>();
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = typeRef.Namespace.IsNil ? string.Empty : reader.GetString(typeRef.Namespace);
            var name = typeRef.Name.IsNil ? string.Empty : reader.GetString(typeRef.Name);
            result.Add((ns, name));
        }
        return result;
    }
}
