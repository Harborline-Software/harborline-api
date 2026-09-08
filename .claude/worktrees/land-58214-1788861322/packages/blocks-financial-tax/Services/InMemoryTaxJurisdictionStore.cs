using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialTax.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialTax.Services;

/// <summary>
/// In-memory implementation of <see cref="ITaxJurisdictionStore"/>
/// suitable for tests + the desktop in-process scenario. SQLite-backed
/// implementation lands in a later persistence-layer hand-off.
/// </summary>
public sealed class InMemoryTaxJurisdictionStore : ITaxJurisdictionStore
{
    private readonly ConcurrentDictionary<TaxJurisdictionId, TaxJurisdiction> _rows = new();

    /// <inheritdoc />
    public Task<TaxJurisdiction?> GetAsync(TaxJurisdictionId id, CancellationToken cancellationToken = default)
    {
        if (_rows.TryGetValue(id, out var row) && row.DeletedAtUtc is null)
        {
            return Task.FromResult<TaxJurisdiction?>(row);
        }
        return Task.FromResult<TaxJurisdiction?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaxJurisdiction>> GetByLevelAsync(JurisdictionLevel level, CancellationToken cancellationToken = default)
    {
        var list = _rows.Values
            .Where(r => r.Level == level && r.DeletedAtUtc is null)
            .ToList();
        return Task.FromResult<IReadOnlyList<TaxJurisdiction>>(list);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaxJurisdiction>> GetChildrenAsync(TaxJurisdictionId parentId, CancellationToken cancellationToken = default)
    {
        var list = _rows.Values
            .Where(r => r.ParentJurisdictionId == parentId && r.DeletedAtUtc is null)
            .ToList();
        return Task.FromResult<IReadOnlyList<TaxJurisdiction>>(list);
    }

    /// <inheritdoc />
    public Task UpsertAsync(TaxJurisdiction jurisdiction, CancellationToken cancellationToken = default)
    {
        var at = jurisdiction.UpdatedAtUtc
            ?? jurisdiction.CreatedAtUtc
            ?? throw new ArgumentException("Tax-jurisdiction writes require an act instant.", nameof(jurisdiction));
        var stamped = jurisdiction with { UpdatedAtUtc = at };
        _rows[stamped.Id] = stamped;
        return Task.CompletedTask;
    }
}
