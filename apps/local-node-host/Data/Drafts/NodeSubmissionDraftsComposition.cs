using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Drafts;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Single source of truth for the node-side D2 submission-draft composition (ADR 0135
/// amendment 2026-07-01). Registers the durable, restart-surviving
/// <see cref="NodeEfSubmissionDraftStore"/> as the <see cref="ISubmissionDraftStore"/> and
/// wires the fail-closed draft services + pre-auth capture buffer.
/// </summary>
/// <remarks>
/// <para>
/// The durable store is registered as a plain <c>AddSingleton</c> BEFORE
/// <see cref="SubmissionDraftServiceCollectionExtensions.AddHarborlineSubmissionDrafts"/> so
/// its <c>TryAddSingleton</c> in-memory default is a no-op — the node uses the durable
/// SQLCipher-backed store, never the process-volatile one (the restart-survival requirement).
/// </para>
/// <para>
/// Requires the caller to have already registered
/// <c>IDbContextFactory&lt;NodeLocalDraftsDbContext&gt;</c> (via
/// <c>AddSqlCipherLocalNodeDbContext</c>), the ADR-0139 erasure registry + ADR-0142
/// legal-hold registry (via <c>AddHarborlineRecoveryCoordinator</c> / <c>AddHarborlineGovernance</c>),
/// and the ADR-0102 party seam (<c>AddHarborlinePartyContext</c> + <c>AddHarborlineTenantContext</c>).
/// </para>
/// </remarks>
public static class NodeSubmissionDraftsComposition
{
    /// <summary>Registers the durable node draft store + the fail-closed draft/capture services.</summary>
    public static IServiceCollection AddNodeSubmissionDrafts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The durable store wins the ISubmissionDraftStore registration (AddSingleton first, so the
        // package's TryAddSingleton in-memory default below is a benign no-op). Also expose the concrete
        // type so a retention sweeper can reach PurgeExpiredAsync.
        services.AddSingleton<NodeEfSubmissionDraftStore>();
        services.AddSingleton<ISubmissionDraftStore>(sp => sp.GetRequiredService<NodeEfSubmissionDraftStore>());

        // The fail-closed draft service + pre-auth capture buffer (TryAdd keeps the durable store above).
        services.AddHarborlineSubmissionDrafts();

        return services;
    }
}
