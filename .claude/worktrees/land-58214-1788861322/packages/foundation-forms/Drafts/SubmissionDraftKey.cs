using System;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// The D2 save-and-resume draft keying tuple (ADR 0135 amendment 2026-07-01):
/// <c>(TenantId, case/subject id, actor's per-org PartyId)</c>. A
/// <b>submission-draft</b> (never a bare "draft") is addressed by exactly this
/// triple — the ONLY key that satisfies cross-device resume, replay-determinism,
/// static analyzability, and multi-org scoping at once, and the only one that
/// expresses "operator continues an incomplete case / reassignment".
/// </summary>
/// <remarks>
/// <para>
/// <b>PartyId is server-derived, never body-supplied.</b> The <see cref="PartyId"/>
/// is resolved through the fail-closed ADR-0102 <c>IPrincipalPartyResolver</c> /
/// <c>IPartyContext</c> against the local roster. A mis-resolved principal MUST
/// block (the resolver's facade throws) — never fall through to
/// <see cref="Guid.Empty"/> and mis-key a draft across tenants. This type only
/// carries an <em>already-resolved</em> party id; <see cref="SubmissionDraftService"/>
/// is where the fail-closed resolution happens.
/// </para>
/// <para>
/// <b>Tenant-scoped by construction.</b> <see cref="TenantId"/> is the first
/// element, so no two tenants can ever share a draft row even if they mint the
/// same case id — the multi-org isolation fence.
/// </para>
/// </remarks>
/// <param name="Tenant">The active tenant the draft belongs to.</param>
/// <param name="Case">The client-mintable case/subject id (<see cref="DraftCaseId"/>).</param>
/// <param name="PartyId">The actor's per-org PartyId (server-derived; never <see cref="Guid.Empty"/>).</param>
public readonly record struct SubmissionDraftKey(TenantId Tenant, DraftCaseId Case, Guid PartyId)
{
    /// <summary>
    /// A stable, order-preserving string form of the key
    /// (<c>"{tenant}|{case}|{party:N}"</c>) — the persistence primary-key surrogate
    /// and cross-device-resume handle. Deterministic across processes / architectures.
    /// </summary>
    public string ToStableString() =>
        string.Concat(Tenant.Value, "|", Case.Value, "|", PartyId.ToString("N"));
}
