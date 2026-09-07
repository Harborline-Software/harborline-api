using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.Banking.Feed;
using Harborline.Api.Blocks.Banking.Import;
using Harborline.Api.Blocks.Banking.Import.Camt;
using Harborline.Api.Blocks.Banking.Import.Csv;
using Harborline.Api.Blocks.Banking.Import.Ofx;
using Harborline.Api.Blocks.Banking.Import.Qif;
using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// Single source of truth for the node-side banking composition (T3 local-first sweep).
/// Registers exactly the banking surface the routes need — the four recoverable Node EF repos +
/// the matching/import services + the fiscal-period repo (for the accept gate) + the Mock bank-feed
/// provider — over an ALREADY-registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> and
/// <c>IJournalStore</c> (the latter wired by <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c>).
/// </summary>
/// <remarks>
/// <para>
/// Extracted (mirroring <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c> /
/// <c>NodeBillWriteComposition.AddNodeBillWrites</c>) so the composition root (<c>Program.cs</c>) and
/// the SC4-T9(b) Layer-2 runtime DI-graph assertion (<c>Sc4RecoverabilityGuardTests</c>) register the
/// EXACT same banking slice — no test/prod drift in WHAT the gate verifies. The security SPOT-CHECK
/// can read this one method to audit every registration the banking path adds.
/// </para>
/// <para>
/// <b>Why register the slice directly instead of <c>AddHarborlineBanking()</c>-then-override?</b> The
/// block's <c>AddHarborlineBanking()</c> registers the in-memory repos + the vendor-provider substrate
/// (with its production-guard assertion). Registering the exact node slice directly keeps the
/// composition SC4-auditable and self-documenting, and avoids the InMemory-then-override
/// double-registration the Bridge uses (the node has no in-memory banking persistence to shadow).
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (extending the
/// security-engineering verdict 2026-06-15 (a)-(d) to the banking path):
/// <list type="bullet">
///   <item>(a) the ONLY persistence sinks are the recoverable <c>local-node.db</c>: the four Node EF
///     banking repos + the node fiscal-period repo. <c>IMatchingRuleRepository</c> is the in-memory
///     block default (there is NO <c>matching_rules</c> table on either provider; rules are
///     built-in-heuristics-only in v1 — the matching engine reads it but it holds no persisted
///     value, so it is not a recoverability sink). This method does NOT register <c>IJournalStore</c>
///     (the posting composition already wired it to the recoverable <c>NodeEfJournalStore</c>); the
///     matching engine reads it read-only for proposal heuristics.</item>
///   <item>(b) NO <c>IDomainEventPublisher</c>/<c>IDomainEventStore</c> is touched — the banking
///     accept/un-match path is propose-never-auto-post (<c>AcceptMatchService</c> posts NO JE; it
///     only mutates MatchLink + StatementLine state in the banking repos).</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here (none of the banking services
///     reference them — the SC4-T9(b) Layer-1 IL scan stays green automatically).</item>
///   <item>(d) the repos resolve to node-resident reads/writes over <c>local-node.db</c>, never a
///     seed-keyed per-team KV store.</item>
/// </list>
/// </para>
/// <para>
/// <b>Bank-feed (Tier-2 mock-first).</b> <see cref="MockBankFeedProvider"/> is the default
/// <see cref="IBankFeedProvider"/> — no real credential egress until a live adapter is opted in. A
/// real AIS adapter (SimpleFIN/Plaid/Teller) slots in behind it via the Tier-2
/// <c>UseVendorProviderIfConfigured&lt;IBankFeedProvider, TReal&gt;(envVarKey)</c> convention
/// (registered AFTER this call, conditional on the provider env-var presence). The
/// <c>IBankFeedProvider</c> contract is AIS read-only (no payment-initiation member); on the node a
/// feed line's opaque <c>RawProviderBlob</c> lives in the encrypted <c>local-node.db</c>.
/// </para>
/// </remarks>
public static class NodeBankingWriteComposition
{
    /// <summary>
    /// Default node CSV column mapping (Date=col0, Description=col1, signed Amount=col2, header row
    /// skipped). v1 ships this generic mapping so CSV import works out-of-box; per-bank column
    /// mappings become capability-plugins (ADR 0123) in a follow-on.
    /// </summary>
    private static readonly CsvColumnMapping DefaultCsvMapping = new(
        DateColumn:        0,
        DescriptionColumn: 1,
        AmountColumn:      2,
        SkipRows:          1);

    /// <summary>
    /// Registers the node-resident banking composition. The caller must already have registered
    /// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the repos' backing) and <c>IJournalStore</c>
    /// (== the node posting store; call <c>AddNodeFinancialPosting</c> first).
    /// </summary>
    public static IServiceCollection AddNodeBankingWrites(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // (a)/(d) The four recoverable Node EF banking repos over local-node.db.
        var accounts = new ConditionalWeakTable<IServiceProvider, Lazy<NodeEfBankAccountRepository>>();
        NodeEfBankAccountRepository Accounts(IServiceProvider provider) => accounts.GetValue(
            provider,
            static sp => new Lazy<NodeEfBankAccountRepository>(() => new NodeEfBankAccountRepository(
                    sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<LocalNodeDbContext>>()),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        services.AddSingleton<IBankAccountRepository>(sp => new NodeBankAccountRepositoryReader(
            Accounts(sp)));
        services.AddSingleton(sp => new NodeBankAccountWriter(
            Accounts(sp),
            sp.GetRequiredService<AuthorizationGate>()));
        services.AddSingleton<IStatementLineRepository, NodeEfStatementLineRepository>();
        services.AddSingleton<IMatchLinkRepository, NodeEfMatchLinkRepository>();
        services.AddSingleton<IReconciliationRepository, NodeEfReconciliationRepository>();

        // Matching rules: built-in-heuristics-only in v1 — no matching_rules table on either provider,
        // so this is the block in-memory default (empty; not a recoverability sink). The matching
        // engine reads it for rule-based proposal hints; v1 ships zero persisted rules.
        services.AddSingleton<IMatchingRuleRepository, InMemoryMatchingRuleRepository>();

        // (a)/(d) Node fiscal-period repo (the periods substrate wired NodeEfPeriodResolver /
        // NodeAccountingPeriodService, NOT IFiscalPeriodRepository — which AcceptMatchService needs).
        services.AddSingleton<IFiscalPeriodRepository, NodeEfFiscalPeriodRepository>();
        services.AddSingleton<ReconciliationLockLease>();

        // Statement-file parsers — reuse the block parsers verbatim. OFX/QIF/CAMT.053 have no deps;
        // CSV needs a column mapping (the v1 default above).
        services.AddSingleton<IStatementFileParser, OfxStatementParser>();
        services.AddSingleton<IStatementFileParser, QifStatementParser>();
        services.AddSingleton<IStatementFileParser, Camt053Parser>();
        services.AddSingleton<IStatementFileParser>(new CsvStatementParser(DefaultCsvMapping));

        // Import pipeline + matching engine services (over the node repos + the node IJournalStore).
        services.AddSingleton<ImportPipelineService>();
        services.AddSingleton<MatchingEngineService>();
        services.AddSingleton<AcceptMatchService>();
        services.AddSingleton<UnMatchService>();
        services.AddSingleton<TransferPairingDetector>();

        // Tier-2 mock-first bank feed. Real AIS adapter slots in behind this via
        // UseVendorProviderIfConfigured(envVarKey) AFTER this call.
        services.AddSingleton<IBankFeedProvider, MockBankFeedProvider>();

        return services;
    }
}

internal sealed class NodeBankAccountRepositoryReader(NodeEfBankAccountRepository inner) : IBankAccountRepository
{
    public Task<BankAccount?> GetByIdAsync(
        TenantId tenantId,
        BankAccountId id,
        CancellationToken ct = default) => inner.GetByIdAsync(tenantId, id, ct);

    public Task<IReadOnlyList<BankAccount>> ListAsync(
        TenantId tenantId,
        bool includeArchived = false,
        CancellationToken ct = default) => inner.ListAsync(tenantId, includeArchived, ct);
}
