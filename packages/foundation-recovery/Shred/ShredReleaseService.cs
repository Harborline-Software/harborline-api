using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// Reference <see cref="IShredReleaseService"/> (ADR 0137 §M-3). Enforces the
/// confirm-time separation-of-duties gate and the ≥2-distinct-approver floor, then
/// delegates key destruction to <see cref="ISubjectErasureService"/>. It deliberately
/// depends ONLY on the erasure SERVICE, never the erasure REGISTRY — so the sole path
/// from a lapsed-retention proposal to a destroyed key runs through the gated service
/// (hold gate + approval floor + dwell). This is what makes "scheduled shred without a
/// ≥2-approver, hold-checked release" structurally impossible.
/// </summary>
public sealed class ShredReleaseService : IShredReleaseService
{
    /// <summary>The ≥2-distinct-approver floor for a release (ADR 0068 §3.1).</summary>
    public const int MinimumApprovers = 2;

    private readonly ISubjectErasureService _erasure;

    /// <summary>Construct over the single key-destruction choke point.</summary>
    public ShredReleaseService(ISubjectErasureService erasure)
        => _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));

    /// <inheritdoc />
    public Task<SubjectErasureResult> ReleaseAsync(ShredReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approvers = request.Approvers ?? Array.Empty<ActorId>();

        // --- SoD gate (confirm-time): the proposer must NOT be an approver. ---
        if (approvers.Any(a => string.Equals(a.Value, request.Proposer.Value, StringComparison.Ordinal)))
        {
            throw new ShredReleaseRejectedException("proposer cannot be an approver");
        }

        // --- ≥2-distinct-approver floor (early, explicit; the erasure service re-enforces it too). ---
        var distinct = approvers.Select(a => a.Value).Distinct(StringComparer.Ordinal).Count();
        if (distinct < MinimumApprovers)
        {
            throw new ShredReleaseRejectedException("approval floor not met");
        }

        // --- Execute through the gated erasure service (hold gate + floor + dwell + audit). ---
        var erasureRequest = new SubjectErasureRequest(
            Tenant: request.Tenant,
            Subject: request.Subject,
            RequestedAt: request.RequestedAt,
            ApprovingActors: approvers,
            LegalBasis: request.LegalBasis);
        return _erasure.EraseAsync(erasureRequest, ct);
    }
}
