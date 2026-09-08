using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// Single source of truth for the node-side durable process-engine composition (ADR 0135 slice 1).
/// Registers the recoverable workflow store + the trigger dispatcher over an ALREADY-registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, plus the workflow entity module (the 3-table schema)
/// and — optionally — the schedule daemon.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>NodeAuditComposition</c> / <c>NodeFinancialPostingComposition</c>: extracted so the
/// composition root (<c>Program.cs</c>) and tests register the EXACT same slice. The engine's only
/// persistence sink is the recoverable <c>local-node.db</c> (the <see cref="NodeEfWorkflowStore"/> rides
/// <c>LocalNodeDbContext</c>) — no seed-keyed KV, no kernel CRDT writer — so the durable engine inherits
/// the SC4-C2 recoverability posture by construction.
/// </para>
/// <para>
/// <b>Handlers are registered by the caller.</b> The two v1 typed handlers
/// (<c>invoice&gt;$5k→approve→post</c>, recurring generation) are slice 2 — this method registers the
/// engine plumbing; the handlers are added alongside it when they land. With zero handlers the dispatcher
/// still resolves (it throws only when a trigger targets an instance whose definition has no handler).
/// </para>
/// </remarks>
public static class NodeWorkflowComposition
{
    /// <summary>Wires the pack-declared Access grant submission projection to its typed workflow handler.</summary>
    public static IServiceCollection AddAccessGrantFormSubmission(this IServiceCollection services)
    {
        services.AddSingleton<AccessGrantFormSubmissionProjection>();
        services.AddSingleton<IFormSubmitProjection>(sp => sp.GetRequiredService<AccessGrantFormSubmissionProjection>());
        services.AddSingleton<Health.IFormSubmissionGate>(sp => sp.GetRequiredService<AccessGrantFormSubmissionProjection>());
        services.AddSingleton<IGrantIssuanceContext>(sp =>
            new NodeGrantIssuanceContext(sp.GetRequiredService<IGrantStore>()));
        services.AddSingleton<IWorkflowStepHandler, GrantIssuanceHandler>();
        return services;
    }
    /// <summary>
    /// Registers the durable workflow ENGINE: the workflow entity module (3-table schema into
    /// <c>LocalNodeDbContext</c>), the recoverable <see cref="NodeEfWorkflowStore"/> as
    /// <see cref="IWorkflowStore"/>, and the <see cref="IWorkflowTriggerDispatcher"/>. The caller must
    /// already have registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> and
    /// <see cref="TimeProvider"/> (<c>AddNodeFinancialPosting</c> registers the latter).
    /// </summary>
    public static IServiceCollection AddNodeWorkflowEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The 3-table schema (workflow_instances / workflow_events / workflow_step_idempotency) into the
        // shared LocalNodeDbContext, so a step's effect + the advance co-commit in one transaction.
        services.AddLocalNodePatternAModule<WorkflowEntityModule>();

        // The recoverable store + the dispatcher (the engine core's DI extension wires the dispatcher).
        services.AddSingleton<IWorkflowStore, NodeEfWorkflowStore>();
        services.AddDurableWorkflowEngine();

        return services;
    }

    /// <summary>
    /// Registers the ADR 0135 A1 DECLARATIVE INTERPRETER (W-8 execution) + its host dependencies: the
    /// server-side confirmation context (<see cref="NodeWorkflowConfirmationContext"/> — the confirmer via
    /// <see cref="IPartyContext"/> + <see cref="IPrincipalKindResolver"/>, the engine proposer), the
    /// principal-kind resolver, and the <c>ledger.post-journal-entry</c> effect factory (through the PUBLIC
    /// delegate seam — the internal factory type is never named). Once registered, the dispatcher runs any
    /// authored + admitted + persisted definition with no typed handler through the interpreter, reaching a CP
    /// effect ONLY via the SoD-gated broker.
    /// <para>
    /// MUST run AFTER <see cref="AddNodeWorkflowEngine"/> (the broker + <see cref="IWorkflowEffectCatalog"/>)
    /// and the definition store (<c>AddEntityStoreWorkflowDefinitionStore</c> — the execution face). Requires
    /// <see cref="TimeProvider"/> (from <c>AddNodeFinancialPosting</c>) + an <see cref="IPartyContext"/>
    /// (from <c>AddNodeDraftPartyContext</c>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddNodeDeclarativeWorkflowExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The server-derived confirmer kind (ADR 0143 D-INV-5) — the local operator is human.
        services.AddSingleton<IPrincipalKindResolver, NodePrincipalKindResolver>();

        // The server-side confirmation context (the confirmer party + kind; the engine proposer).
        services.AddSingleton<IWorkflowConfirmationContext, NodeWorkflowConfirmationContext>();

        // The ledger.post-journal-entry effect factory, registered through the PUBLIC delegate seam so the host
        // never names the internal IWorkflowEffectFactory (SC2 F-1). ledger.post-journal-entry is CP in the
        // authority registry, so the broker gates it behind a human confirm; the closure stages a balanced JE
        // onto the atomic advance (build invariant #1).
        services.AddWorkflowEffectFactory(
            NodeLedgerPostingEffect.CapabilityRef,
            WorkflowEffectReach.Internal,
            (sp, request) => NodeLedgerPostingEffect.Build(
                request,
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialLedger.Services.IJournalPostingService>()));

        // The interpreter itself — the dispatcher resolves it as the fallback for definitions with no handler.
        services.AddDeclarativeWorkflowInterpreter();

        // The GENERIC pending-CP-effect-confirmation read model (the Harborline App "Effect Confirmations" surface) —
        // a pure read over the workflow tables for interpreter-parked CP confirmations (cp-approval-basis), no
        // new schema. Distinct from the typed-vertical NodeParkedTaskQueryReadModel (invoice/kg).
        services.AddSingleton(sp =>
            new NodeWorkflowConfirmationReadModel(
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()));

        // The executed-run / effect-ledger report read model (P6 — declarative reporting over the substrate,
        // NOT a new package): a pure read over completed/terminal instances + their effect events.
        services.AddSingleton(sp =>
            new NodeWorkflowRunReportReadModel(
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()));

        return services;
    }

    /// <summary>
    /// Registers the <c>schedule</c>-trigger <see cref="WorkflowScheduleDaemon"/> as a hosted service.
    /// Separate from <see cref="AddNodeWorkflowEngine"/> because the daemon needs an
    /// <see cref="IWorkflowScheduleSource"/> (a slice-2 recurring source, or a test source) the caller
    /// supplies; without one the engine still serves event / human-action / dependency-complete triggers.
    /// </summary>
    public static IServiceCollection AddNodeWorkflowScheduleDaemon(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<WorkflowScheduleDaemon>();
        return services;
    }

    /// <summary>
    /// Registers the TWO v1 typed handlers (ADR 0135 slice 2) + their host-side financial contexts, and the
    /// production recurring <see cref="IWorkflowScheduleSource"/> (RRULE over the recoverable schedule rows):
    /// <list type="bullet">
    ///   <item>Handler A — <see cref="InvoiceApprovalHandler"/> over the v1 threshold decision table (D7) +
    ///     <see cref="NodeInvoiceApprovalContext"/> (the JE post effect / FE-1 preview).</item>
    ///   <item>Handler B — <see cref="RecurringGenerationHandler"/> + <see cref="NodeRecurringGenerationContext"/>
    ///     (the deterministic occurrence JE effect, bug-1337 derivation).</item>
    ///   <item>The <see cref="NodeRecurringScheduleSource"/> driving Handler B from the daemon.</item>
    /// </list>
    /// MUST run AFTER <see cref="AddNodeWorkflowEngine"/> (the dispatcher resolves <c>IEnumerable&lt;IWorkflowStepHandler&gt;</c>).
    /// Requires <see cref="TimeProvider"/> (registered by <c>AddNodeFinancialPosting</c>) + an
    /// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>.
    /// </summary>
    public static IServiceCollection AddNodeWorkflowHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // RRULE expansion engine (foundation-scheduling, in-memory; idempotent TryAdd) — shared with the
        // recurring-invoice composition.
        services.AddFoundationScheduling();

        // ── Handler A — invoice > $5k → approve → post ──
        // The v1 effective-dated threshold decision table (D7). A SINGLE version pinned at the v1 cutover —
        // an in-flight instance pins this version at instantiation; a future threshold change adds a NEW
        // effective-dated version without retroactively altering pinned instances.
        services.AddSingleton(_ => NodeWorkflowDefinitions.InvoiceApprovalThresholdTable());

        // LIVE invoice-approval context — the post effect stages the REAL invoice's issue (the balanced
        // Debit-AR / Credit-income JE + the Draft → Issued status) directly onto the workflow advance's
        // unit-of-work (ONE SQLite transaction; build invariant #1), vs the self-contained single-line JE
        // NodeInvoiceApprovalContext stages for the engine's own slice tests. This makes the over-threshold
        // post produce the same GL + status outcome the direct-post route produces — only deferred to
        // approve-time. No-double-post by construction: the invoice-issue route never posts inline for the
        // over-threshold case, so this effect is the over-threshold invoice's ONLY issuer.
        services.AddSingleton<IInvoiceApprovalContext>(sp =>
            new NodeLiveInvoiceApprovalContext(
                // MTW-2 2612-C — the approve-time invoice-issue JE (staged on the workflow uow, not via
                // NodeEfJournalStore) picks up the node-signed attribution envelope when the audit
                // enlister is wired. The same request-scoped attribution source stamps the issued
                // invoice's UpdatedBy; both remain optional so workflow-only composition resolves.
                sp.GetService<Data.Audit.INodeAuditWriteEnlister>(),
                sp.GetService<Data.Audit.INodeCallerAttributionSource>()));
        services.AddSingleton<IWorkflowStepHandler>(sp =>
            new InvoiceApprovalHandler(
                sp.GetRequiredService<ThresholdDecisionTable>(),
                sp.GetRequiredService<IInvoiceApprovalContext>()));

        // ── Handler B — recurring generation on the schedule trigger ──
        services.AddSingleton<IRecurringGenerationContext>(sp =>
            new NodeRecurringGenerationContext());
        services.AddSingleton<IWorkflowStepHandler>(sp =>
            new RecurringGenerationHandler(sp.GetRequiredService<IRecurringGenerationContext>()));

        // The production schedule source for Handler B (RRULE over the recoverable recurring-generation rows).
        services.AddSingleton<IWorkflowScheduleSource>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var rrule = sp.GetRequiredService<IRruleExpansionService>();
            var definitions = sp.GetService<IWorkflowDefinitionExecutionStore>();
            return definitions is null
                ? new NodeRecurringScheduleSource(factory, rrule)
                : new NodeRecurringScheduleSource(factory, rrule, definitions);
        });

        // The instantiation surface (ADR 0135 v1) — CREATES Process instances from real domain events / rows
        // (an issued invoice → an invoice-approval Process; an Active RecurringInvoiceSchedule →
        // a recurring-generation Process the daemon then drives). Completes the engine end-to-end: the
        // handlers + dispatcher + daemon advance instances; THIS is what puts instances in the store. Depends
        // only on the IWorkflowStore + the LocalNodeDbContext factory (both already registered).
        services.AddSingleton(sp =>
            new NodeWorkflowInstantiationService(
                sp.GetRequiredService<IWorkflowStore>(),
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()));

        // ── The invoice-issue cutover + the parked-task (Ask-bar Inbox) read model (ADR 0135 vertical flow) ──
        //
        // The cutover is what the invoice-issue route consults: an over-threshold Draft is routed through the
        // engine (instantiate the invoice-approval Process + drive decide → PARK) instead of posting inline,
        // and it is the resume coordinator the approval-task /action route drives (approve / reject /
        // send-back). It needs the instantiation service (above) + the IWorkflowTriggerDispatcher
        // (AddDurableWorkflowEngine, via AddNodeWorkflowEngine).
        services.AddSingleton(sp =>
            new NodeInvoiceApprovalCutover(
                sp.GetRequiredService<NodeWorkflowInstantiationService>(),
                sp.GetRequiredService<IWorkflowTriggerDispatcher>(),
                sp.GetRequiredService<IWorkflowStore>(),
                sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>(),
                // Ticket 272 slice 3 — the approve path decides separation of duty ONCE through the shared
                // engine and records that decision on the audit entry through the one mapping seam.
                sp.GetRequiredService<Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine>(),
                sp.GetRequiredService<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>(),
                sp.GetRequiredService<Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner>().Signer,
                // Ticket 272 slice 5 — the fifth approval fact, the posting period, is READ from the same
                // IPeriodResolver JournalPostingService gates the post on (AddNodeFinancialPosting), for the
                // live invoice's chart and issue date (AddNodeInvoiceWrites). Never a constant.
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialLedger.Services.IPeriodResolver>(),
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialAr.Services.IInvoiceRepository>()));

        // ── Handler C — a grounded GraphRAG proposed CP action → human-CP-gate → execute (ADR 0135 KG-search
        //    Slice 2-actions, G-G4). A proposed CP action ALWAYS parks; the ONLY path to execution is a human
        //    approve (the model PROPOSES, the human ACTS). The execute effect runs the EXISTING CP path —
        //    for the v1 draft-journal-entry kind, it stages a Draft JE onto the advance's unit-of-work. ──
        services.AddSingleton<IKgActionApprovalContext>(_ => new NodeKgActionApprovalContext());
        services.AddSingleton<IWorkflowStepHandler>(sp =>
            new GraphRagProposalHandler(sp.GetRequiredService<IKgActionApprovalContext>()));

        // The kg-action park coordinator — the ONLY bridge from a grounded proposed CP action to the engine.
        // It PARKS (never executes); the resume route drives approve / reject / send-back. (G-G4 lives in its
        // shape: its only proposal verb is ParkForApprovalAsync — there is no Execute(proposal) here.)
        services.AddSingleton(sp =>
            new NodeKgActionApprovalCutover(
                sp.GetRequiredService<NodeWorkflowInstantiationService>(),
                sp.GetRequiredService<IWorkflowTriggerDispatcher>(),
                sp.GetRequiredService<TimeProvider>()));

        // The parked-task read model — a pure read over the workflow tables for the Ask-bar Inbox
        // (parked invoice-approval AND kg-action-approval tasks + their FE-1 basis payload). No new schema.
        services.AddSingleton(sp =>
            new NodeParkedTaskQueryReadModel(
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()));

        return services;
    }
}
