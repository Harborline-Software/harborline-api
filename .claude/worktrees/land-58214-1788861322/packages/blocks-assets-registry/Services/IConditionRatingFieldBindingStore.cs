using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Forms.Models;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Tenant-scoped registry of the <see cref="ConditionRatingFieldBinding"/>s in force for a form
/// definition — the configuration the Wave-2 condition-capture projector reads to decide whether a
/// submission carries a condition-rating field it must project (ADR 0101 Rev 3.1 Wave 2 / A3).
/// </summary>
/// <remarks>
/// Bindings are tenant config (which of a tenant's forms rate which entities, on what scale), so the
/// store is keyed by <c>(tenant, form)</c> and rejects the system / default tenant sentinel. This is
/// deliberately a small keyed lookup, not a general plugin registry — it exists only to activate the
/// one shipped field kind by binding.
/// </remarks>
public interface IConditionRatingFieldBindingStore
{
    /// <summary>Registers a binding for a tenant's form (idempotent per identical binding).</summary>
    Task RegisterAsync(TenantId tenant, ConditionRatingFieldBinding binding, CancellationToken cancellationToken = default);

    /// <summary>
    /// The condition-rating bindings a tenant has declared on <paramref name="form"/>; empty when the
    /// form carries none (the projector's no-op case).
    /// </summary>
    Task<IReadOnlyList<ConditionRatingFieldBinding>> GetForFormAsync(
        TenantId tenant, FormDefinitionId form, CancellationToken cancellationToken = default);
}
