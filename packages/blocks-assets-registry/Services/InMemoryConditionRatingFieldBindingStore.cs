using System.Collections.Concurrent;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IConditionRatingFieldBindingStore"/> for tests, demos, and the
/// Wave-2 node host. A persistence-backed implementation lives behind the same interface.
/// </summary>
public sealed class InMemoryConditionRatingFieldBindingStore : IConditionRatingFieldBindingStore
{
    private readonly ConcurrentDictionary<(TenantId Tenant, FormDefinitionId Form), List<ConditionRatingFieldBinding>> _byForm = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task RegisterAsync(TenantId tenant, ConditionRatingFieldBinding binding, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(binding);

        lock (_gate)
        {
            var list = _byForm.GetOrAdd((tenant, binding.FormDefinition), static _ => new List<ConditionRatingFieldBinding>());
            if (!list.Contains(binding))
            {
                list.Add(binding);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConditionRatingFieldBinding>> GetForFormAsync(
        TenantId tenant, FormDefinitionId form, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        lock (_gate)
        {
            IReadOnlyList<ConditionRatingFieldBinding> result = _byForm.TryGetValue((tenant, form), out var list)
                ? list.ToArray()
                : Array.Empty<ConditionRatingFieldBinding>();
            return Task.FromResult(result);
        }
    }
}
