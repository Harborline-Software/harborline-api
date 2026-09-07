using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Coordination;

using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side recurring-invoice composition (T2b recurring-invoice
/// node-flip). Registers exactly the services <see cref="NodeEfRecurringInvoiceService"/> needs — the
/// RRULE expansion service (<c>foundation-scheduling</c>) + the recurring-invoice service over the
/// recoverable <see cref="LocalNodeDbContext"/> + its route-handler accessor — over an ALREADY-registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, <c>IInvoicePostingService</c>, and
/// <c>IInvoiceRepository</c> (the latter two wired by
/// <see cref="NodeInvoiceWriteComposition.AddNodeInvoiceWrites"/> in Step 2b).
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and any future runtime DI-graph assertion
/// register the EXACT same recurring slice — no test/prod drift. It mirrors
/// <see cref="NodeInvoiceWriteComposition.AddNodeInvoiceWrites"/>.
/// </para>
/// <para>
/// <b>The generation engine is the Harborline block.</b> RRULE expansion comes from
/// <see cref="IRruleExpansionService"/> (registered idempotently via
/// <see cref="SchedulingServiceCollectionExtensions.AddFoundationScheduling"/>); draft → issue → balanced
/// JE goes through the node-resident <see cref="IInvoicePostingService"/> (the Step-2b posting service over
/// the recoverable <see cref="NodeEfJournalStore"/>); drafts are upserted via the node
/// <see cref="IInvoiceRepository"/> (<see cref="NodeEfInvoiceRepository"/>). This composition therefore adds
/// no GL-writing primitive of its own beyond the schedule rows.
/// </para>
/// <para>
/// <b>SC4-C2.</b> Every persistence sink reachable from this slice is the recoverable
/// <c>local-node.db</c> — the schedule rows via <see cref="LocalNodeDbContext"/>, the issued-invoice
/// drafts via <see cref="NodeEfInvoiceRepository"/>, and (transitively, through the node posting service)
/// the issue JEs via <see cref="NodeEfJournalStore"/>. No kernel CRDT writer / per-team event log is
/// reachable, so the SC4-T9(b) gate is unaffected. The RRULE service is pure/in-memory (holds no
/// irreplaceable financial value).
/// </para>
/// </remarks>
public static class NodeRecurringInvoiceComposition
{
    /// <summary>
    /// Registers the node-resident recurring-invoice service + accessor. The caller is responsible for
    /// having already registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the schedule store),
    /// <c>IInvoiceRepository</c> (== <see cref="NodeEfInvoiceRepository"/>), and
    /// <c>IInvoicePostingService</c> (== the Step-2b node posting service) — call
    /// <c>AddNodeInvoiceWrites</c> first.
    /// </summary>
    public static IServiceCollection AddNodeRecurringInvoices(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        // RRULE expansion engine (foundation-scheduling, in-memory; idempotent TryAdd).
        services.AddFoundationScheduling();

        // The node-resident recurring-invoice service over the recoverable local-node.db. Composes over
        // the already-registered IRruleExpansionService + node IInvoicePostingService + node
        // IInvoiceRepository + the LocalNodeDbContext factory.
        services.AddSingleton<IRecurringInvoiceService>(sp =>
            new NodeEfRecurringInvoiceService(
                contextFactory: sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(),
                rrule:          sp.GetRequiredService<IRruleExpansionService>(),
                posting:        sp.GetRequiredService<IInvoicePostingService>(),
                invoices:       sp.GetRequiredService<IInvoiceRepository>()));

        // bug-1337 / ADR 0135 SC1 — the recurring-invoice idempotency enlister. NodeEfJournalStore
        // resolves the recurring adapter through its declared Platform registry; an
        // issue JE's idempotency record (the schedule's GeneratedInvoices map entry) joins the JE's
        // local-node.db transaction whenever an ambient RecurringInvoiceWriteScope is active. A no-op for
        // every non-recurring JE post, so manual JE / bills / payments / payroll paths are unaffected.
        services.AddSingleton<INodeRecurringInvoiceWriteEnlister, NodeRecurringInvoiceWriteEnlister>();
        services.AddSingleton<IWriteEnlistment>(sp =>
            (IWriteEnlistment)sp.GetRequiredService<INodeRecurringInvoiceWriteEnlister>());

        return services;
    }
}
