using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>The coordinated invariants declared by the node journal-post operation.</summary>
public static class NodeWriteInvariants
{
    /// <summary>Rejects writes from a stale home before any other invariant is staged.</summary>
    public static WriteInvariant HomeEpoch { get; } = new("home-epoch");

    /// <summary>Maintains the tenant audit hash-chain ordering.</summary>
    public static WriteInvariant Audit { get; } = new("audit");

    /// <summary>Co-commits recurring-invoice occurrence idempotency.</summary>
    public static WriteInvariant RecurringInvoice { get; } = new("recurring-invoice");

    /// <summary>Co-commits the issued-invoice status transition.</summary>
    public static WriteInvariant IssuedInvoice { get; } = new("issued-invoice");
}

/// <summary>The journal chokepoint's declaration of every coordinated invariant it requires.</summary>
public static class NodeJournalWriteOperation
{
    /// <summary>The declared journal-post operation.</summary>
    public static DeclaredWriteOperation Post { get; } = new(
        "journal.post",
        [
            NodeWriteInvariants.HomeEpoch,
            NodeWriteInvariants.Audit,
            NodeWriteInvariants.RecurringInvoice,
            NodeWriteInvariants.IssuedInvoice,
        ]);
}

/// <summary>Builds a complete adapter set for directly constructed node journal stores.</summary>
public static class NodeJournalWriteAdapters
{
    /// <summary>
    /// Creates all four required adapters, allowing a test or specialized host to replace an adapter
    /// without turning any declared invariant back into an optional slot.
    /// </summary>
    public static IReadOnlyList<IWriteEnlistment> Create(
        IWriteEnlistment? audit = null,
        IWriteEnlistment? recurringInvoice = null,
        IWriteEnlistment? issuedInvoice = null,
        IWriteEnlistment? homeEpoch = null) =>
        [
            homeEpoch ?? new HomeEpochFenceEnlister(),
            audit ?? new NodeAuditWriteEnlister(),
            recurringInvoice ?? new NodeRecurringInvoiceWriteEnlister(),
            issuedInvoice ?? new NodeIssuedInvoiceWriteEnlister(),
        ];
}

/// <summary>
/// The node journal store's staged unit of work. Domain content stays in this host adapter type and does
/// not enter the Platform <see cref="IWriteEnlistment"/> signature.
/// </summary>
public sealed class NodeJournalWriteUnitOfWork : StagedWriteUnitOfWork
{
    /// <summary>Creates the staged journal unit over its in-flight EF context.</summary>
    public NodeJournalWriteUnitOfWork(
        LocalNodeDbContext context,
        JournalEntry entry,
        AuthorizationDecision decision)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
    }

    /// <summary>The in-flight node context shared by every adapter.</summary>
    public LocalNodeDbContext Context { get; }

    /// <summary>The journal entry being staged by the host chokepoint.</summary>
    public JournalEntry Entry { get; }

    /// <summary>The exact allowed decision carried from the journal posting gate.</summary>
    public AuthorizationDecision Decision { get; }
}
