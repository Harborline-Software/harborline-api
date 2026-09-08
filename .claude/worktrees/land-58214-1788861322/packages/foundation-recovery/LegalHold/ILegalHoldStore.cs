using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The append-only persistence for the legal-hold registry (ADR 0142 §D1 / §D4).
/// Holds and releases are both <em>appended</em> — a hold is never mutated or
/// deleted, and a release is a new record that references a hold by id. This
/// mirrors the crypto-shred erasure registry's no-un-erase discipline and keeps
/// the discovery audit trail intact.
/// </summary>
/// <remarks>
/// The default <see cref="InMemoryLegalHoldStore"/> is restart-volatile; a forgotten
/// hold silently <em>un-holds</em> a subject (the spoliation risk), so a host that
/// runs any shred path MUST register a durable, append-only implementation and call
/// <c>RequireDurableLegalHoldStore()</c> at its composition root. The surface is
/// <see cref="ValueTask"/>-returning so a durable / cross-node implementation can
/// persist without sync-over-async on the fail-closed shred gate.
/// </remarks>
public interface ILegalHoldStore
{
    /// <summary>Append a placed hold.</summary>
    ValueTask AppendHoldAsync(LegalHoldEntry entry, CancellationToken ct = default);

    /// <summary>Append a release (the append-only record that ends a hold).</summary>
    ValueTask AppendReleaseAsync(LegalHoldRelease release, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when at least one ACTIVE (placed, not-yet-released) hold
    /// for <paramref name="tenant"/> matches <paramref name="heldRef"/> exactly. This
    /// is the query the fail-closed shred gate resolves.
    /// </summary>
    ValueTask<bool> HasActiveHoldAsync(TenantId tenant, HeldRef heldRef, CancellationToken ct = default);

    /// <summary>Find a placed hold by id within a tenant, or <c>null</c> if none.</summary>
    ValueTask<LegalHoldEntry?> FindHoldAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default);

    /// <summary>Returns <c>true</c> when the hold identified by <paramref name="holdId"/> has a release record.</summary>
    ValueTask<bool> IsReleasedAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default);

    /// <summary>Enumerate all ACTIVE holds for <paramref name="tenant"/> (for operational review). Order is unspecified.</summary>
    ValueTask<IReadOnlyList<LegalHoldEntry>> ListActiveAsync(TenantId tenant, CancellationToken ct = default);
}
