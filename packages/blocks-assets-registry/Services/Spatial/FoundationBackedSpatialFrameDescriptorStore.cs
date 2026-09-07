using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Foundation.Crypto;

using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The <b>package-side adapter</b> behind <see cref="ISpatialFrameDescriptorStore"/> — the
/// <c>FoundationBackedRegistryAuditLog</c> shape (ADR 0101 Rev 3.2 [A13]): it applies the
/// <c>internal</c> <see cref="RegistryTenantGuard"/> and delegates persistence to the injected
/// foundation port. It participates in the mint as a CALLBACK on the in-flight context inside the
/// host's fenced transaction: guard → tip-read → normalize → sign → stage, all under the write
/// lock — signature production happens INSIDE the lock, after the tip read that fixes
/// <c>frameEpoch</c>/<c>previousEpoch</c>, never before it (ADR 0168 D2-A4(b)/(c)).
/// </summary>
public sealed class FoundationBackedSpatialFrameDescriptorStore : ISpatialFrameDescriptorStore
{
    /// <summary>The ADR 0141 fixed length unit — the only permitted value.</summary>
    public const string FixedLengthUnit = "metre";

    private readonly ISpatialFrameDescriptorPort _port;
    private readonly IFrameEpochAuthority _authority;
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _clock;

    /// <summary>Wires the adapter over the foundation port, the CP-5 authority seam and the node signer.</summary>
    public FoundationBackedSpatialFrameDescriptorStore(
        ISpatialFrameDescriptorPort port,
        IFrameEpochAuthority authority,
        IOperationSigner signer,
        TimeProvider? clock = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<SpatialFrameDescriptor> MintAsync(
        SpatialFrameMintRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RegistryTenantGuard.Require(request.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Anchor.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AxisConvention);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginDescription);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PreviousEpoch);
        if (!string.Equals(request.LengthUnit, FixedLengthUnit, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"lengthUnit must be '{FixedLengthUnit}' (FIXED, ADR 0141).", nameof(request));
        }
        if (request.Georeference is { Orientation: not null, PoseBasis: null or "" })
        {
            throw new ArgumentException(
                "georeference.poseBasis is REQUIRED when orientation is present (ADR 0168 D2).",
                nameof(request));
        }

        // [A14] — the single normalization chokepoint, BEFORE anything reads, signs or stages.
        var frameCode = SpatialFrameCodes.Normalize(request.FrameCode);

        // Defence-in-depth pre-flight refusal ([A10]); the enforcing check is the port's
        // in-transaction home-claim re-read.
        await _authority.AssertMintAuthorityAsync(request.Tenant, ct).ConfigureAwait(false);

        return await _port.MintAsync(
            new SpatialFrameMintCommand(request.Tenant, request.Anchor, frameCode),
            (context, token) => StageAsync(request, frameCode, context, token),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SpatialFrameDescriptor?> FindAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode, long frameEpoch,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(context);
        return _port.FindAsync(tenant, anchor, SpatialFrameCodes.Normalize(frameCode), frameEpoch, context, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SpatialFrameDescriptor>> ListAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(context);
        return _port.ListAsync(tenant, anchor, SpatialFrameCodes.Normalize(frameCode), context, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SpatialFrameQuarantineRecord>> ListQuarantinedAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(context);
        return _port.ListQuarantinedAsync(tenant, anchor, SpatialFrameCodes.Normalize(frameCode), context, ct);
    }

    /// <summary>
    /// The in-transaction mint body (runs under the port's held write lock). The layer-2 conflict
    /// pre-check compares the caller's believed predecessor against the tip fixed under the lock;
    /// a stale belief stages ONLY the quarantine row (0168 D2-A6).
    /// </summary>
    private async ValueTask<SpatialFrameMintStaging> StageAsync(
        SpatialFrameMintRequest request,
        string frameCode,
        SpatialFrameMintContext context,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        if (request.PreviousEpoch != context.TipEpoch)
        {
            return SpatialFrameMintStaging.Conflict(new SpatialFrameQuarantineRecord(
                Id: Guid.NewGuid(),
                TenantId: request.Tenant,
                Anchor: request.Anchor,
                FrameCode: frameCode,
                AttemptedEpoch: request.PreviousEpoch + 1,
                TipEpochAtDetection: context.TipEpoch,
                Reason: SpatialFrameQuarantineReason.EpochConflict,
                AxisConvention: request.AxisConvention,
                OriginDescription: request.OriginDescription,
                LengthUnit: request.LengthUnit,
                Georeference: request.Georeference,
                DetectedAt: now));
        }

        var frameEpoch = context.TipEpoch + 1; // genesis is 1; each mint appends previous + 1 (D2-A3)
        var contentHash = SpatialFrameContentHashing.Compute(
            request.AxisConvention, request.OriginDescription, request.LengthUnit, request.Georeference);

        var payload = new SpatialFrameMintSignaturePayload(
            TenantId: request.Tenant.Value,
            Anchor: request.Anchor.Value,
            FrameCode: frameCode,
            FrameEpoch: frameEpoch,
            PreviousEpoch: context.TipEpoch,
            HomeDeviceId: context.HomeDeviceId,
            GrantingHomeEpoch: context.GrantingHomeEpoch,
            ContentHash: contentHash);

        // Truncate to epoch-ms so the stored and signed instants are byte-aligned (the signable
        // envelope serializes issuedAt as integer epoch-ms) — the RosterSigning/HomeEpochRecord pin.
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();
        var signed = await _signer.SignAsync(payload, issuedAt, nonce, ct).ConfigureAwait(false);

        return SpatialFrameMintStaging.Mint(new SpatialFrameDescriptor(
            TenantId: request.Tenant,
            Anchor: request.Anchor,
            FrameCode: frameCode,
            FrameEpoch: frameEpoch,
            AxisConvention: request.AxisConvention,
            OriginDescription: request.OriginDescription,
            LengthUnit: request.LengthUnit,
            Georeference: request.Georeference,
            Attestation: new SpatialFrameMintAttestation(
                Issuer: signed.IssuerId.ToBase64Url(),
                Nonce: nonce,
                IssuedAt: issuedAt,
                Signature: signed.Signature.ToBase64Url(),
                HomeDeviceId: context.HomeDeviceId,
                GrantingHomeEpoch: context.GrantingHomeEpoch,
                PreviousEpoch: context.TipEpoch,
                ContentHash: contentHash)));
    }
}
