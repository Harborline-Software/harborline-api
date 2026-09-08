using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// EF Core–backed <see cref="IHomeEpochStore"/> over the recoverable <c>local-node.db</c>. Reads the
/// tenant's current home epoch and drives the verified bump-on-promotion advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verification is forge-proof by reconstruction.</b> <see cref="AdvanceAsync"/> reconstructs the
/// canonical <see cref="HomeEpochSignaturePayload"/> from the proposed row's own fields and re-verifies the
/// Ed25519 signature against the stamped issuer key — the SAME discipline
/// <c>RosterSigning.VerifyAdmission</c> applies. A bump whose home device, epoch number, predecessor, or
/// promotion kind was tampered reconstructs different signable bytes ⇒ the check fails ⇒
/// <see cref="HomeEpochAdvanceRejectedException"/> ⇒ nothing persists.
/// </para>
/// <para>
/// <b>The authority check (is the issuer an in-roster admin holding home-promotion authority?) is layered
/// ABOVE this store</b>, exactly as <c>RosterSigning</c> verifies a signature here but the receiving
/// roster checks <c>members:revoke</c> where the revocation is APPLIED. This store proves the bump is
/// cryptographically authentic + monotonic + meets the multi-actor floor; the caller (the CP promotion
/// human-task) supplies issuer keys it has already confirmed are roster-bound home admins.
/// </para>
/// </remarks>
public sealed class HomeEfHomeEpochStore : IHomeEpochStore
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly IOperationVerifier _verifier;

    /// <summary>Construct bound to the local-node EF context factory + the foundation Ed25519 verifier.</summary>
    public HomeEfHomeEpochStore(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IOperationVerifier verifier)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <inheritdoc />
    public async Task<HomeEpochRecord?> GetCurrentEpochAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await CurrentTipAsync(ctx, tenantId, tracked: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentException.ThrowIfNullOrEmpty(proposed.TenantId);
        ArgumentException.ThrowIfNullOrEmpty(proposed.HomeDeviceId);
        ArgumentException.ThrowIfNullOrEmpty(proposed.IssuerId);
        ArgumentException.ThrowIfNullOrEmpty(proposed.Signature);

        // ── 1. Signature (forge-proof) ──────────────────────────────────────────────────────────────
        // Reconstruct the canonical payload from the proposed row's OWN fields and verify against the
        // stamped issuer key. A tampered field reconstructs different bytes ⇒ verify fails.
        if (!VerifyBumpSignature(proposed.IssuerId, proposed.Signature, proposed))
        {
            throw new HomeEpochAdvanceRejectedException(
                $"Home-epoch bump to {proposed.EpochNumber} for tenant '{proposed.TenantId}' has an invalid " +
                $"proposer signature — rejected (forged or tampered bump).");
        }

        // ── 2. Multi-actor floor (G-5 / ADR 0068 §3.1) ──────────────────────────────────────────────
        if (proposed.PromotionKind == HomePromotionKind.RecoveryFailover)
        {
            if (string.IsNullOrEmpty(proposed.CoApproverIssuerId) || string.IsNullOrEmpty(proposed.CoApproverSignature))
            {
                throw new HomeEpochAdvanceRejectedException(
                    $"Recovery-failover bump to {proposed.EpochNumber} for tenant '{proposed.TenantId}' is " +
                    $"missing the mandatory co-approver (G-5 multi-actor floor) — rejected.");
            }

            // The co-approver MUST be DISTINCT from the proposer (no self-approval, ADR 0068 §3.1 (c)).
            if (string.Equals(proposed.CoApproverIssuerId, proposed.IssuerId, StringComparison.Ordinal))
            {
                throw new HomeEpochAdvanceRejectedException(
                    $"Recovery-failover bump to {proposed.EpochNumber} for tenant '{proposed.TenantId}' has a " +
                    $"co-approver identical to the proposer — the multi-actor floor requires two DISTINCT " +
                    $"admins; rejected.");
            }

            // The co-approver signs the SAME canonical payload — two distinct admins attest THIS promotion.
            if (!VerifyBumpSignature(proposed.CoApproverIssuerId, proposed.CoApproverSignature, proposed))
            {
                throw new HomeEpochAdvanceRejectedException(
                    $"Recovery-failover bump to {proposed.EpochNumber} for tenant '{proposed.TenantId}' has an " +
                    $"invalid co-approver signature — rejected.");
            }
        }

        // ── 3. Strict monotonicity + persist (atomic) ───────────────────────────────────────────────
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var current = await CurrentTipAsync(ctx, proposed.TenantId, tracked: false, ct).ConfigureAwait(false);
        var currentNumber = current?.EpochNumber ?? 0L;

        // The new epoch must be exactly current+1 and name current as its predecessor. This is the
        // monotonic guard the schema-epoch state machine has in-memory, made durable + tenant-keyed here.
        if (proposed.EpochNumber != currentNumber + 1L || proposed.PreviousEpochNumber != currentNumber)
        {
            throw new HomeEpochAdvanceRejectedException(
                $"Home-epoch bump for tenant '{proposed.TenantId}' is not strictly monotonic: proposed " +
                $"(epoch {proposed.EpochNumber}, previous {proposed.PreviousEpochNumber}) but the current epoch " +
                $"is {currentNumber} (expected epoch {currentNumber + 1L}, previous {currentNumber}) — rejected.");
        }

        ctx.Set<HomeEpochRecord>().Add(proposed);

        // The composite PK (TenantId, EpochNumber) is the durable backstop: a concurrent advance racing
        // the same epoch number trips a unique-constraint violation on save — fail-closed, never a
        // double-home. (Single-device installs do not race; this is defence-in-depth.)
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the tenant's current home-epoch tip (highest <c>EpochNumber</c>) on a context.</summary>
    private static async Task<HomeEpochRecord?> CurrentTipAsync(
        LocalNodeDbContext ctx, string tenantId, bool tracked, CancellationToken ct)
    {
        var query = ctx.Set<HomeEpochRecord>()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.EpochNumber)
            .AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }
        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconstructs the canonical <see cref="HomeEpochSignaturePayload"/> from <paramref name="record"/>'s
    /// own fields and verifies an Ed25519 signature over it against <paramref name="issuerIdB64"/>. Fail-closed
    /// (returns false) on any malformed key / signature.
    /// </summary>
    private bool VerifyBumpSignature(string issuerIdB64, string signatureB64, HomeEpochRecord record)
    {
        try
        {
            var payload = HomeEpochSignaturePayload.For(
                tenantId: record.TenantId,
                epochNumber: record.EpochNumber,
                previousEpochNumber: record.PreviousEpochNumber,
                homeDeviceId: record.HomeDeviceId,
                promotionKind: record.PromotionKind);

            var op = new SignedOperation<HomeEpochSignaturePayload>(
                Payload: payload,
                IssuerId: PrincipalId.FromBase64Url(issuerIdB64),
                IssuedAt: record.IssuedAt,
                Nonce: record.Nonce,
                Signature: Signature.FromBase64Url(signatureB64));
            return _verifier.Verify(op);
        }
        catch (FormatException)
        {
            // Malformed key / signature ⇒ not verifiable ⇒ not authentic (fail-closed).
            return false;
        }
    }
}
