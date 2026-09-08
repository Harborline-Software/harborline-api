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
using Harborline.Api.Foundation.Integrations.DependencyInjection;

namespace Harborline.Api.Blocks.Banking.DependencyInjection;

/// <summary>
/// DI composition-root extensions for the <c>blocks-banking</c> domain block.
/// Per ADR 0112 / ADR 0096 Mock-first discipline.
/// </summary>
public static class BankingServiceCollectionExtensions
{
    /// <summary>
    /// Registers all in-memory implementations for the banking block,
    /// the file-import parsers, and the import pipeline service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call <c>AddHarborlineVendorProviderSubstrate()</c> BEFORE this method to
    /// initialize the <c>IMockVendorEnvVarRegistry</c> and
    /// <c>MockProviderProductionGuardAssertion</c> (ADR 0096 §D1c).
    /// </para>
    /// <para>
    /// To swap in a real live-feed adapter, call
    /// <c>UseVendorProviderIfConfigured&lt;IBankFeedProvider, TReal&gt;(envVarKey)</c>
    /// (from <c>VendorProviderServiceCollectionExtensions</c>) after this call
    /// with the provider env-var key (e.g. <c>"SIMPLEFIN_ACCESS_URL"</c>).
    /// </para>
    /// <para>
    /// <strong>CSV column mapping:</strong> the default CSV parser uses a mapping
    /// that is replaced by registering a custom <see cref="CsvColumnMapping"/>
    /// before calling this method.
    /// Callers that import CSV files MUST register a bank-specific mapping that matches
    /// their bank's export format before calling <see cref="ImportPipelineService"/>.
    /// The registration here registers the parser factory WITHOUT a mapping; the
    /// caller is expected to construct a <see cref="CsvStatementParser"/> with the
    /// correct mapping and register it as <see cref="IStatementFileParser"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineBanking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // --- In-memory repositories ---
        services.AddSingleton<IBankAccountRepository, InMemoryBankAccountRepository>();
        services.AddSingleton<IStatementLineRepository, InMemoryStatementLineRepository>();
        services.AddSingleton<IMatchLinkRepository, InMemoryMatchLinkRepository>();
        services.AddSingleton<IReconciliationRepository, InMemoryReconciliationRepository>();
        services.AddSingleton<IMatchingRuleRepository, InMemoryMatchingRuleRepository>();
        services.AddSingleton<ReconciliationLockLease>();

        // --- File-import parsers ---
        // OFX/QFX and QIF parsers have no external dependencies; register as singletons.
        services.AddSingleton<IStatementFileParser, OfxStatementParser>();
        services.AddSingleton<IStatementFileParser, QifStatementParser>();
        services.AddSingleton<IStatementFileParser, Camt053Parser>();
        // CsvStatementParser requires a CsvColumnMapping; it is NOT registered here.
        // Callers add: services.AddSingleton<IStatementFileParser>(new CsvStatementParser(mapping))

        // --- Import pipeline service ---
        services.AddSingleton<ImportPipelineService>();

        // --- Matching engine services (H-3, ADR 0112 §4/5) ---
        // MatchingEngineService requires IJournalStore from blocks-financial-ledger;
        // callers MUST register a IJournalStore before calling AddHarborlineBanking,
        // or register it separately in the same DI container.
        services.AddSingleton<MatchingEngineService>();
        services.AddSingleton<AcceptMatchService>();
        services.AddSingleton<UnMatchService>();
        services.AddSingleton<TransferPairingDetector>();

        // --- IBankFeedProvider: Mock-first (ADR 0096 + ADR 0112 Part 2) ---
        services.AddHarborlineVendorProvider<IBankFeedProvider, MockBankFeedProvider>();

        return services;
    }
}
