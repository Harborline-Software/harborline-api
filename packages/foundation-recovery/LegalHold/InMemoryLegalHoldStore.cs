using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The restart-volatile in-memory reference <see cref="ILegalHoldStore"/> (ADR 0142
/// §D4). Append-only: holds and releases accumulate and are never removed. Suitable
/// as a default for tests and for hosts that run NO shred path; a host that runs a
/// shred path MUST override this with a durable store and call
/// <c>RequireDurableLegalHoldStore()</c> — a lost hold silently un-holds a subject on
/// restart, which is a spoliation risk.
/// </summary>
public sealed class InMemoryLegalHoldStore : ILegalHoldStore
{
    // Holds keyed by (tenant, holdId); releases as a set of released (tenant, holdId).
    private readonly ConcurrentDictionary<(string Tenant, string Hold), LegalHoldEntry> _holds = new();
    private readonly ConcurrentDictionary<(string Tenant, string Hold), byte> _released = new();

    /// <inheritdoc />
    public ValueTask AppendHoldAsync(LegalHoldEntry entry, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(entry);
        _holds[(entry.TenantId.Value, entry.HoldId.Value)] = entry;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AppendReleaseAsync(LegalHoldRelease release, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(release);
        _released[(release.TenantId.Value, release.HoldId.Value)] = 1;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> HasActiveHoldAsync(TenantId tenant, HeldRef heldRef, CancellationToken ct = default)
    {
        var canonical = heldRef.Canonical;
        var held = _holds.Values.Any(h =>
            h.TenantId.Value == tenant.Value
            && h.HeldRef.Canonical == canonical
            && !_released.ContainsKey((h.TenantId.Value, h.HoldId.Value)));
        return ValueTask.FromResult(held);
    }

    /// <inheritdoc />
    public ValueTask<LegalHoldEntry?> FindHoldAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default)
    {
        _holds.TryGetValue((tenant.Value, holdId.Value), out var entry);
        return ValueTask.FromResult(entry);
    }

    /// <inheritdoc />
    public ValueTask<bool> IsReleasedAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default)
        => ValueTask.FromResult(_released.ContainsKey((tenant.Value, holdId.Value)));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LegalHoldEntry>> ListActiveAsync(TenantId tenant, CancellationToken ct = default)
    {
        IReadOnlyList<LegalHoldEntry> active = _holds.Values
            .Where(h => h.TenantId.Value == tenant.Value
                        && !_released.ContainsKey((h.TenantId.Value, h.HoldId.Value)))
            .ToList();
        return ValueTask.FromResult(active);
    }
}
