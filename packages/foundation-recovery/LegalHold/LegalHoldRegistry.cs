using System;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// Reference <see cref="ILegalHoldRegistry"/> over an <see cref="ILegalHoldStore"/>
/// (ADR 0142 §D3). Resolves active holds from the store and enforces the fail-closed
/// polarity: any fault reaching the store is caught and answered as <c>held</c>, so
/// an unreachable registry can never be mistaken for "nothing held".
/// </summary>
public sealed class LegalHoldRegistry : ILegalHoldRegistry
{
    private readonly ILegalHoldStore _store;

    /// <summary>Construct over the append-only hold store.</summary>
    public LegalHoldRegistry(ILegalHoldStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async ValueTask<bool> IsHeldAsync(TenantId tenant, HeldRef heldRef, CancellationToken ct = default)
    {
        try
        {
            return await _store.HasActiveHoldAsync(tenant, heldRef, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-closed: an unreachable / erroring hold store MUST answer "held" so
            // the shred gate refuses. (ADR 0142 §D3 — unreachable ⇒ held.)
            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> IsSubjectHeldAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
        => IsHeldAsync(tenant, HeldRef.ForSubject(subject), ct);
}
