using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.FinancialLedger.Services;

namespace Harborline.Api.Blocks.FinancialLedger.DependencyInjection;

/// <summary>DI registration for the journal entry query read model.</summary>
public static class JournalEntryQueryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryJournalEntryQueryReadModel"/> as the
    /// <see cref="IJournalEntryQueryReadModel"/> implementation.
    /// The caller must also register <see cref="IJournalStore"/> (typically
    /// <see cref="InMemoryJournalStore"/>).
    /// </summary>
    public static IServiceCollection AddInMemoryJournalEntryQueryReadModel(
        this IServiceCollection services)
    {
        services.AddSingleton<IJournalEntryQueryReadModel, InMemoryJournalEntryQueryReadModel>();
        return services;
    }
}
