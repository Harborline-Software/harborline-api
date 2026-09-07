using System.Collections.Concurrent;
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// In-memory implementation of <see cref="ILeaseSubLedgerLinkRepository"/> (ADR 0120 PR-D).
/// Keyed on <c>(TenantId.Value, LeaseId.Value)</c>. Thread-safe via a
/// <c>ConcurrentDictionary</c> (matches the financial-cluster in-memory store pattern).
/// </summary>
public sealed class InMemoryLeaseSubLedgerLinkRepository : ILeaseSubLedgerLinkRepository
{
    // Composite key: "{tenantId}:{leaseId}"
    private readonly ConcurrentDictionary<string, LeaseSubLedgerLink> _store = new();

    private static string Key(TenantId tenantId, LeaseId leaseId) =>
        $"{tenantId.Value}:{leaseId.Value}";

    /// <inheritdoc />
    public Task UpsertAsync(
        TenantId tenantId,
        LeaseSubLedgerLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"LeaseSubLedgerLink.TenantId '{link.TenantId.Value}' does not match tenantId '{tenantId.Value}'.",
                nameof(link));

        _store[Key(tenantId, link.LeaseId)] = link;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LeaseSubLedgerLink?> GetByLeaseAsync(
        TenantId tenantId,
        LeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(Key(tenantId, leaseId), out var link);
        // Uniform-404: return null on miss OR cross-tenant (key encodes both).
        return Task.FromResult(link);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LeaseSubLedgerLink>> ListAllAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var prefix = tenantId.Value + ":";
        IReadOnlyList<LeaseSubLedgerLink> results = _store.Values
            .Where(l => l.TenantId.Equals(tenantId))
            .ToList();
        return Task.FromResult(results);
    }
}
