using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Health;
// NodePersistenceConflict narrows the layer-3 catch to UNIQUE/PK violations only.

using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// The host's durable <b>port implementation</b> behind
/// <see cref="ISpatialFrameDescriptorPort"/> — named as the port so it cannot be mistaken for the
/// store interface (ADR 0101 Rev 3.2 [A13]). Owns the fenced mint transaction (0168 D2-A4(b)):
/// <c>BEGIN IMMEDIATE</c> via <see cref="HomeEpochFenceTransaction.RunAsync"/>, the ENFORCING
/// in-transaction re-read of the tenant's <see cref="HomeEpochRecord"/> tip (refuse unless a record
/// exists AND its <c>HomeDeviceId</c> is this node's Ed25519 signing principal), the additive
/// <see cref="HomeEpochFence.AssertNotStaleAsync"/>, the frame-epoch tip read, the package callback
/// on the in-flight context, and the single <c>SaveChangesAsync</c> — all under the held write lock.
/// </summary>
/// <remarks>
/// <para><b>The audit append is host-owned and post-commit ([A13]/[A11]):</b> the registry audit
/// substrate is not transaction-enlistable on <see cref="LocalNodeDbContext"/>, so on BOTH D2-A6
/// paths the append follows the commit decision — never precedes it — and an append failure is
/// surfaced, not swallowed. Every op's <c>Detail</c> carries ONLY the identity triple.</para>
/// <para><b>Quarantine survives the rollback on both paths (0168 D2-A6):</b> the layer-2 conflict
/// stages ONLY the quarantine row and COMMITS; the layer-3 PK backstop writes the quarantine in a
/// SECOND committed transaction after the rollback. The typed rejection is raised AFTER
/// <c>RunAsync</c> returns (or after the backstop quarantine commits).</para>
/// <para><b>Process-local mutual exclusion</b> (the <c>NodeEfInvoiceNumberingService</c> shape) is
/// the defence against two in-flight mints racing the same tip read within the process; the
/// IMMEDIATE lock serializes across connections; the composite PK is the storage backstop.</para>
/// </remarks>
public sealed class NodeEfSpatialFrameDescriptorPort : ISpatialFrameDescriptorPort
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly IRegistryAuditLog _audit;
    private readonly SpatialFramePiiFieldSealer _sealer;
    private readonly string _deviceId;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Wires the port over the shared local-node context factory, the registry X-AUDIT journal, the
    /// CP-4 governed-field sealer (REQUIRED — a port without it would persist cleartext PII) and
    /// the node principal signer — whose Ed25519 public key IS the device identity the home-claim
    /// equality check reads (the ADR 0101 precondition pin: never <c>NodeIdentity.NodeId</c>).
    /// </summary>
    public NodeEfSpatialFrameDescriptorPort(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRegistryAuditLog audit,
        SpatialFramePiiFieldSealer sealer,
        NodePrincipalSigner nodeSigner,
        TimeProvider? clock = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        ArgumentNullException.ThrowIfNull(nodeSigner);
        _deviceId = nodeSigner.NodePublicKey;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<SpatialFrameDescriptor> MintAsync(
        SpatialFrameMintCommand command,
        SpatialFrameMintCallback callback,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(callback);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MintLockedAsync(command, callback, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SpatialFrameDescriptor> MintLockedAsync(
        SpatialFrameMintCommand command,
        SpatialFrameMintCallback callback,
        CancellationToken ct)
    {
        SpatialFrameMintStaging? staging = null;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        try
        {
            await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
            {
                // (a)+(b) — the ENFORCING check: re-read the tenant's home tip ON the in-flight
                // context, under the held write lock. Absence is refusal, not a grant.
                var home = await ctx.Set<HomeEpochRecord>()
                    .AsNoTracking()
                    .Where(r => r.TenantId == command.Tenant.Value)
                    .OrderByDescending(r => r.EpochNumber)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (home is null)
                {
                    throw new FrameEpochMintRefusedException(
                        command.Tenant.Value,
                        "no HomeEpochRecord exists for the tenant — a positively-asserted home claim "
                        + "is required; absence of home state is refusal, not a grant (ADR 0168 D2-A4(a)).");
                }

                if (!string.Equals(home.HomeDeviceId, _deviceId, StringComparison.Ordinal))
                {
                    throw new FrameEpochMintRefusedException(
                        command.Tenant.Value,
                        $"the tenant's home device as of home epoch {home.EpochNumber} is not this node.");
                }

                // Additive defence in depth on top of the re-read — never a substitute for it.
                using var scope = HomeEpochWriteScope.Enter(
                    new PendingHomeEpochAssertion(command.Tenant.Value, home.EpochNumber, _deviceId));
                await HomeEpochFence.AssertNotStaleAsync(ctx, HomeEpochWriteScope.Current, ct)
                    .ConfigureAwait(false);

                // The frame-epoch tip, fixed under the write lock (0 for an empty series).
                var tip = await ctx.Set<SpatialFrameDescriptorRow>()
                    .AsNoTracking()
                    .Where(r => r.TenantId == command.Tenant.Value
                                && r.AnchorId == command.Anchor.Value
                                && r.FrameCode == command.FrameCode)
                    .MaxAsync(r => (long?)r.FrameEpoch, ct)
                    .ConfigureAwait(false) ?? 0L;

                // The package callback on the in-flight context: guard → tip-read → normalize →
                // sign → stage (normalization already applied at the adapter chokepoint).
                staging = await callback(
                        new SpatialFrameMintContext(tip, home.HomeDeviceId, home.EpochNumber), ct)
                    .ConfigureAwait(false);

                if (staging.Descriptor is not null)
                {
                    // CP-4: the two governed fields are sealed at this storage boundary — the
                    // domain object (and the already-signed contentHash preimage) stay cleartext.
                    ctx.Add(await ToRowSealedAsync(staging.Descriptor, ct).ConfigureAwait(false));
                }
                else
                {
                    // Layer-2 conflict: stage ONLY the quarantine row — the descriptor is never
                    // persisted; RunAsync COMMITS this artifact (0168 D2-A6). Same CP-4 cell.
                    ctx.Add(await ToRowSealedAsync(staging.Quarantine!, ct).ConfigureAwait(false));
                }

                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
            when (staging?.Descriptor is not null && NodePersistenceConflict.IsDuplicate(ex))
        {
            // Layer-3 — the composite-PK storage backstop poisoned and rolled back the fenced
            // transaction. ONLY a UNIQUE / PRIMARY KEY violation is the backstop firing — any
            // other DbUpdateException (SQLITE_BUSY, disk full, NOT NULL, a converter fault)
            // rethrows untouched: classifying an environmental failure as StorageConflict would
            // commit a FALSE quarantine and an irreversible Op.Reject journal row.
            // The quarantine survives the rollback via a SECOND committed transaction.
            var quarantine = SpatialFrameQuarantineRecord.ForStorageConflict(
                staging.Descriptor, _clock.GetUtcNow());
            await using (var quarantineCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                quarantineCtx.Add(await ToRowSealedAsync(quarantine, ct).ConfigureAwait(false));
                await quarantineCtx.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // Audit append follows the commit decision; the typed rejection follows the append.
            AppendConflictAudit(quarantine);
            throw new SpatialFrameEpochMintRejectedException(quarantine);
        }

        // RunAsync returned ⇒ the commit decision is made. Audit rides here, on both paths ([A11]).
        if (staging!.Descriptor is not null)
        {
            var minted = staging.Descriptor;
            _audit.Append(
                minted.TenantId,
                Subject(minted.Anchor.Value, minted.FrameCode),
                RegistryOp.SpatialFrameDescriptorMinted,
                new Instant(_clock.GetUtcNow()),
                actorRef: _deviceId,
                detail: TripleDetail(minted.Anchor.Value, minted.FrameCode, minted.FrameEpoch));
            return minted;
        }

        var rejected = staging.Quarantine!;
        AppendConflictAudit(rejected);
        throw new SpatialFrameEpochMintRejectedException(rejected);
    }

    /// <inheritdoc />
    public async Task<SpatialFrameDescriptor?> FindAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode, long frameEpoch,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Set<SpatialFrameDescriptorRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                r => r.TenantId == tenant.Value && r.AnchorId == anchor.Value
                     && r.FrameCode == frameCode && r.FrameEpoch == frameEpoch,
                ct)
            .ConfigureAwait(false);
        return row is null ? null : await FromRowAsync(row, context, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpatialFrameDescriptor>> ListAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.Set<SpatialFrameDescriptorRow>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenant.Value && r.AnchorId == anchor.Value && r.FrameCode == frameCode)
            .OrderBy(r => r.FrameEpoch)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = new List<SpatialFrameDescriptor>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await FromRowAsync(row, context, ct).ConfigureAwait(false));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpatialFrameQuarantineRecord>> ListQuarantinedAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.Set<SpatialFrameQuarantineRow>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenant.Value && r.AnchorId == anchor.Value && r.FrameCode == frameCode)
            .OrderBy(r => r.DetectedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = new List<SpatialFrameQuarantineRecord>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await FromQuarantineRowAsync(row, context, ct).ConfigureAwait(false));
        }
        return result;
    }

    private void AppendConflictAudit(SpatialFrameQuarantineRecord quarantine) =>
        _audit.Append(
            quarantine.TenantId,
            Subject(quarantine.Anchor.Value, quarantine.FrameCode),
            RegistryOp.SpatialFrameDescriptorConflictDetected,
            new Instant(_clock.GetUtcNow()),
            actorRef: _deviceId,
            // Identity triple ONLY — never originDescription, never georeference ordinates ([A11]).
            detail: TripleDetail(quarantine.Anchor.Value, quarantine.FrameCode, quarantine.AttemptedEpoch));

    private static string Subject(string anchorId, string frameCode) =>
        $"spatial-frame:{anchorId}:{frameCode}";

    private static string TripleDetail(string anchorId, string frameCode, long frameEpoch) =>
        $"anchor={anchorId};frameCode={frameCode};frameEpoch={frameEpoch}";

    // CP-4 (ADR 0168 D2-A8): the two governed fields — OriginDescription + Georeference — are
    // sealed into the tenant-DEK EncryptedField envelope at THIS storage boundary, on both row
    // families. ContentHash is the signed preimage and persists CLEARTEXT (the pinned trap:
    // sealing or MAC'ing it would break every historical mint signature irreversibly). The
    // identity triple + AxisConvention stay cleartext for keying/display.
    private async Task<SpatialFrameDescriptorRow> ToRowSealedAsync(
        SpatialFrameDescriptor d, CancellationToken ct) => new()
    {
        TenantId = d.TenantId.Value,
        AnchorId = d.Anchor.Value,
        FrameCode = d.FrameCode,
        FrameEpoch = d.FrameEpoch,
        AxisConvention = d.AxisConvention,
        // A staged descriptor ALWAYS carries the governed prose (mint validates non-null);
        // null here would mean a REDACTED read-back is being re-persisted — refuse loudly.
        OriginDescription = (await _sealer.SealAsync(
            d.OriginDescription ?? throw new InvalidOperationException(
                "Refusing to persist a descriptor with a withheld (redacted) originDescription."),
            d.TenantId, ct).ConfigureAwait(false))!,
        LengthUnit = d.LengthUnit,
        GeoreferenceJson = await _sealer
            .SealAsync(SerializeGeoreference(d.Georeference), d.TenantId, ct).ConfigureAwait(false),
        Issuer = d.Attestation.Issuer,
        Nonce = d.Attestation.Nonce,
        IssuedAt = d.Attestation.IssuedAt,
        Signature = d.Attestation.Signature ?? throw new InvalidOperationException(
            "Refusing to persist a descriptor with a withheld (redacted) attestation signature."),
        HomeDeviceId = d.Attestation.HomeDeviceId,
        GrantingHomeEpoch = d.Attestation.GrantingHomeEpoch,
        PreviousEpoch = d.Attestation.PreviousEpoch,
        ContentHash = d.Attestation.ContentHash ?? throw new InvalidOperationException(
            "Refusing to persist a descriptor with a withheld (redacted) attestation contentHash."),
    };

    // The store-level Redact@Read / Audit@Read seam (CIC ruling 2026-08-06; pii binding).
    // Redacted: the governed cells are WITHHELD (null) without ever being decrypted — no plaintext
    // in memory, no audit row owed (audit is bounded to actual unsealing). Privileged: decrypt-at-
    // read, so contentHash + the mint signature re-verify from the row (the row-alone invariant),
    // then ONE identity-triple Audit@Read row is appended BEFORE the descriptor is surfaced. The
    // append is FATAL on failure — surfacing PII is the grant, and a grant that cannot be audited
    // is not surfaced (the PR 3716 asymmetry; contrast the mint DENIAL audit, which is post-fact).
    private async Task<SpatialFrameDescriptor> FromRowAsync(
        SpatialFrameDescriptorRow r, SpatialFrameReadContext context, CancellationToken ct)
    {
        string? origin = null;
        SpatialFrameGeoreference? georeference = null;
        if (context.UnsealsGoverned)
        {
            origin = (await _sealer.UnsealAsync(r.OriginDescription, new TenantId(r.TenantId), ct)
                .ConfigureAwait(false))!;
            georeference = DeserializeGeoreference(
                await _sealer.UnsealAsync(r.GeoreferenceJson, new TenantId(r.TenantId), ct).ConfigureAwait(false));
            AppendUnsealAudit(new TenantId(r.TenantId), context, r.AnchorId, r.FrameCode, r.FrameEpoch);
        }

        return new SpatialFrameDescriptor(
            TenantId: new TenantId(r.TenantId),
            Anchor: new RegistryEntityId(r.AnchorId),
            FrameCode: r.FrameCode,
            FrameEpoch: r.FrameEpoch,
            AxisConvention: r.AxisConvention,
            OriginDescription: origin,
            LengthUnit: r.LengthUnit,
            Georeference: georeference,
            Attestation: new SpatialFrameMintAttestation(
                Issuer: r.Issuer,
                Nonce: r.Nonce,
                IssuedAt: r.IssuedAt,
                // F2: the cleartext hash + signature are confirmation oracles over the withheld
                // cells (guess -> hash -> verify), so a redacted read withholds them too.
                // Read-boundary only: the row keeps both; the signed preimage is never touched.
                Signature: context.UnsealsGoverned ? r.Signature : null,
                HomeDeviceId: r.HomeDeviceId,
                GrantingHomeEpoch: r.GrantingHomeEpoch,
                PreviousEpoch: r.PreviousEpoch,
                ContentHash: context.UnsealsGoverned ? r.ContentHash : null));
    }

    /// <summary>
    /// The Audit@Read append — one record per row whose governed cells were ACTUALLY unsealed.
    /// Rides the same registry audit authority as the mint path. Detail is the identity triple
    /// ONLY ([A11]) — never the unsealed values. Thrown failures propagate: the caller's read
    /// fails and nothing already unsealed in this call is surfaced (fatal withhold).
    /// </summary>
    private void AppendUnsealAudit(
        TenantId tenant, SpatialFrameReadContext context, string anchorId, string frameCode, long frameEpoch) =>
        _audit.Append(
            tenant,
            Subject(anchorId, frameCode),
            RegistryOp.SpatialFrameDescriptorPiiUnsealed,
            new Instant(_clock.GetUtcNow()),
            actorRef: context.ActorRef,
            detail: TripleDetail(anchorId, frameCode, frameEpoch));

    private async Task<SpatialFrameQuarantineRow> ToRowSealedAsync(
        SpatialFrameQuarantineRecord q, CancellationToken ct) => new()
    {
        Id = q.Id,
        TenantId = q.TenantId.Value,
        AnchorId = q.Anchor.Value,
        FrameCode = q.FrameCode,
        AttemptedEpoch = q.AttemptedEpoch,
        TipEpochAtDetection = q.TipEpochAtDetection,
        Reason = q.Reason.ToString(),
        AxisConvention = q.AxisConvention,
        OriginDescription = (await _sealer.SealAsync(
            q.OriginDescription ?? throw new InvalidOperationException(
                "Refusing to persist a quarantine record with a withheld (redacted) originDescription."),
            q.TenantId, ct).ConfigureAwait(false))!,
        LengthUnit = q.LengthUnit,
        GeoreferenceJson = await _sealer
            .SealAsync(SerializeGeoreference(q.Georeference), q.TenantId, ct).ConfigureAwait(false),
        DetectedAt = q.DetectedAt,
    };

    // Same Redact@Read / Audit@Read seam for the quarantine family (same CP-4 cell). The audited
    // epoch is the ATTEMPTED epoch — the triple the quarantined content was minted against.
    private async Task<SpatialFrameQuarantineRecord> FromQuarantineRowAsync(
        SpatialFrameQuarantineRow r, SpatialFrameReadContext context, CancellationToken ct)
    {
        string? origin = null;
        SpatialFrameGeoreference? georeference = null;
        if (context.UnsealsGoverned)
        {
            origin = (await _sealer.UnsealAsync(r.OriginDescription, new TenantId(r.TenantId), ct)
                .ConfigureAwait(false))!;
            georeference = DeserializeGeoreference(
                await _sealer.UnsealAsync(r.GeoreferenceJson, new TenantId(r.TenantId), ct).ConfigureAwait(false));
            AppendUnsealAudit(new TenantId(r.TenantId), context, r.AnchorId, r.FrameCode, r.AttemptedEpoch);
        }

        return new SpatialFrameQuarantineRecord(
            Id: r.Id,
            TenantId: new TenantId(r.TenantId),
            Anchor: new RegistryEntityId(r.AnchorId),
            FrameCode: r.FrameCode,
            AttemptedEpoch: r.AttemptedEpoch,
            TipEpochAtDetection: r.TipEpochAtDetection,
            Reason: Enum.Parse<SpatialFrameQuarantineReason>(r.Reason),
            AxisConvention: r.AxisConvention,
            OriginDescription: origin,
            LengthUnit: r.LengthUnit,
            Georeference: georeference,
            DetectedAt: r.DetectedAt);
    }

    private static string? SerializeGeoreference(SpatialFrameGeoreference? georeference) =>
        georeference is null ? null : JsonSerializer.Serialize(georeference);

    private static SpatialFrameGeoreference? DeserializeGeoreference(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<SpatialFrameGeoreference>(json);
}
