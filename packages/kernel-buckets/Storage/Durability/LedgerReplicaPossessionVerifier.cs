namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the reference <see cref="IReplicaPossessionVerifier"/>: it lists the raw claims from the
/// <see cref="IReplicaPossessionLedger"/> and CONFIRMS each one through an <see cref="IReplicaPossessionProbe"/>,
/// returning ONLY the probe-confirmed claims. A ledger entry the probe refuses (e.g. stale) is dropped — that is
/// the verify-before-evict, not trust-the-ledger, discipline in code.
/// </summary>
public sealed class LedgerReplicaPossessionVerifier : IReplicaPossessionVerifier
{
    private readonly IReplicaPossessionLedger _ledger;
    private readonly IReplicaPossessionProbe _probe;

    /// <summary>Construct over a claim ledger and the probe that re-confirms each claim.</summary>
    public LedgerReplicaPossessionVerifier(IReplicaPossessionLedger ledger, IReplicaPossessionProbe probe)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConfirmedReplica>> VerifyPossessionAsync(DurableRef record, CancellationToken ct)
    {
        var claims = await _ledger.ListAsync(record, ct).ConfigureAwait(false);
        if (claims.Count == 0)
        {
            return Array.Empty<ConfirmedReplica>();
        }

        var confirmed = new List<ConfirmedReplica>(claims.Count);
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.ConfirmAsync(claim, ct).ConfigureAwait(false))
            {
                confirmed.Add(claim);
            }
        }
        return confirmed;
    }
}
