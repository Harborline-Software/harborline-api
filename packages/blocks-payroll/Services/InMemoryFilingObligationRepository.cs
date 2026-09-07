using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// In-memory <see cref="IFilingObligationRepository"/> suitable for v1 testing and demos.
/// Thread-safe for single-process use.
/// </summary>
public sealed class InMemoryFilingObligationRepository : IFilingObligationRepository
{
    private readonly List<FilingObligation> _store = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<FilingObligation?> GetAsync(
        TenantId tenantId,
        FilingObligationId id,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _store.FirstOrDefault(o => o.TenantId.Equals(tenantId) && o.Id.Equals(id));
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        TenantId tenantId,
        FilingObligation entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"FilingObligation '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        lock (_gate)
        {
            var idx = _store.FindIndex(o => o.TenantId.Equals(tenantId) && o.Id.Equals(entity.Id));
            if (idx >= 0)
                _store[idx] = entity;
            else
                _store.Add(entity);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FilingObligation>> ListByDueDateAsync(
        TenantId tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FilingObligation> result = _store
                .Where(o => o.TenantId.Equals(tenantId) && o.DueDate >= from && o.DueDate <= to)
                .OrderBy(o => o.DueDate)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FilingObligation>> ListOutstandingAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FilingObligation> result = _store
                .Where(o => o.TenantId.Equals(tenantId) && !o.IsComplete)
                .OrderBy(o => o.DueDate)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
