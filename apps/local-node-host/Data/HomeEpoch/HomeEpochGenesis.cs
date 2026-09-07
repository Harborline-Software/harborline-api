using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Builds and writes the <b>genesis</b> <see cref="HomeEpochRecord"/> (epoch 1) for a tenant — the
/// ADR 0101 Rev 3.2 precondition-2 / ADR 0168 D2-A4(a) install-time write that makes "this single
/// device IS the home" a RECORDED, signed fact instead of an inferred absence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a recorded fact.</b> Before this write, a single-device install had NO home-epoch row and
/// "no failover has occurred" was inferred from the absence (<c>GetCurrentEpochAsync == null</c>).
/// An absence cannot be signed, synced, or fenced against. The genesis row (epoch 1, previous 0,
/// <see cref="HomeEpochRecord.HomeDeviceId"/> = the installing device) is signed through the SAME
/// canonical <see cref="HomeEpochSignaturePayload"/> path every later promotion uses, so the very
/// first multi-home promotion (MD-3/MD-4, out of scope here) starts from a verifiable epoch-1
/// predecessor rather than a special empty case.
/// </para>
/// <para>
/// <b>Signer.</b> The genesis is signed by the node's canonical principal signer (the install's
/// root-seed-derived Ed25519 identity) as a <see cref="HomePromotionKind.Genesis"/> record — the
/// dedicated additive kind ADR 0101 Rev 3.2 precondition 2 requires, never an overloaded transfer
/// kind; the single-admin floor applies (no contest at install time — the G-5 multi-actor floor is
/// for recovery failovers only). <see cref="HomeEfHomeEpochStore.AdvanceAsync"/> re-verifies the
/// signature before persisting, so even the genesis row goes through the full fail-closed path.
/// </para>
/// <para>
/// <b>Idempotent.</b> If the tenant already has ANY home epoch (a previous run wrote genesis, or a
/// promotion has since advanced past it), this is a no-op — re-running enrollment never mints a
/// second genesis. A concurrent racing writer is absorbed the same way: the store's monotonicity
/// check (plus the composite-PK backstop) rejects the loser, and the rejection is swallowed IF AND
/// ONLY IF a current epoch is then visible (someone did establish it); any other rejection rethrows.
/// </para>
/// </remarks>
public static class HomeEpochGenesis
{
    /// <summary>
    /// Ensures the tenant has a home-epoch genesis row, writing the signed epoch-1 record naming
    /// <paramref name="homeDeviceId"/> as the home when none exists. Returns <see langword="true"/>
    /// when THIS call wrote the genesis, <see langword="false"/> when an epoch already existed
    /// (idempotent no-op).
    /// </summary>
    /// <param name="store">The durable home-epoch store (verifies + appends the record).</param>
    /// <param name="signer">The signer whose identity issues the genesis (the node principal signer).</param>
    /// <param name="tenantId">The tenant the home epoch is scoped to.</param>
    /// <param name="homeDeviceId">The installing device's stable id — the genesis home.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<bool> EnsureWrittenAsync(
        IHomeEpochStore store,
        IOperationSigner signer,
        string tenantId,
        string homeDeviceId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(homeDeviceId);

        // Idempotency gate: ANY existing epoch (genesis or later) means the recorded fact exists.
        var current = await store.GetCurrentEpochAsync(tenantId, ct).ConfigureAwait(false);
        if (current is not null)
        {
            return false;
        }

        var payload = HomeEpochSignaturePayload.For(
            tenantId: tenantId,
            epochNumber: 1L,
            previousEpochNumber: 0L,
            homeDeviceId: homeDeviceId,
            promotionKind: HomePromotionKind.Genesis);

        // Truncate to epoch-ms so the signed instant round-trips byte-identically through storage:
        // verification reconstructs the payload from the STORED IssuedAt, so a precision-losing
        // round-trip would break the signature (RosterSigning uses the same epoch-ms discipline).
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            at.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();

        var signed = await signer.SignAsync(payload, issuedAt, nonce, ct).ConfigureAwait(false);

        var genesis = new HomeEpochRecord
        {
            TenantId = tenantId,
            EpochNumber = 1L,
            PreviousEpochNumber = 0L,
            HomeDeviceId = homeDeviceId,
            PromotionKind = HomePromotionKind.Genesis,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = signed.IssuerId.ToBase64Url(),
            Signature = signed.Signature.ToBase64Url(),
            CoApproverIssuerId = null,
            CoApproverSignature = null,
        };

        try
        {
            await store.AdvanceAsync(genesis, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HomeEpochAdvanceRejectedException
            or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // A concurrent writer may have established the epoch between our read and append. The
            // loser surfaces EITHER as the store's monotonicity rejection (raced before our append's
            // tip re-read) OR as the composite-PK backstop, which EF raises as DbUpdateException,
            // not the store's own exception. If an epoch NOW exists the recorded fact is in place —
            // idempotent outcome; anything else is a real failure and rethrows.
            var raced = await store.GetCurrentEpochAsync(tenantId, ct).ConfigureAwait(false);
            if (raced is not null)
            {
                return false;
            }
            throw;
        }
    }

    internal static Task<bool> EnsureWrittenAsync(
        IHomeEpochStore store,
        IOperationSigner signer,
        string tenantId,
        string homeDeviceId,
        CancellationToken ct = default)
        => EnsureWrittenAsync(store, signer, tenantId, homeDeviceId, DateTimeOffset.UnixEpoch, ct);
}
