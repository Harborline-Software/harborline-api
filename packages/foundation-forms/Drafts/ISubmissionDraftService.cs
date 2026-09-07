using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// The fail-closed facade for save-and-resume submission-drafts (ADR 0135 amendment
/// 2026-07-01 — D2). This is the ONE seam that turns "who is calling" into the
/// server-derived <c>(TenantId, PartyId)</c> half of the draft key — via the
/// fail-closed ADR-0102 <c>IPartyContext</c> — and combines it with the caller's
/// client-mintable <see cref="DraftCaseId"/>. No method accepts a body-supplied party
/// id or tenant; both are derived from the ambient authenticated principal, so a draft
/// can never be mis-keyed across tenants.
/// </summary>
/// <remarks>
/// <b>Fail-closed.</b> Every operation resolves the party FIRST. If the principal
/// resolves to no party (a mis-provisioned actor) or no principal is present, the
/// resolver's facade throws <c>PrincipalPartyResolutionException</c> and the operation
/// blocks — it never falls through to <see cref="Guid.Empty"/> and mis-keys the draft.
/// </remarks>
public interface ISubmissionDraftService
{
    /// <summary>
    /// Saves (inserts or replaces) the actor's draft for <paramref name="caseId"/> on
    /// <paramref name="formId"/>. Resolves the tenant + party fail-closed, preserves the
    /// original <c>CreatedAt</c> across re-saves, and returns the resolved key.
    /// </summary>
    Task<SubmissionDraftKey> SaveDraftAsync(
        FormDefinitionId formId,
        DraftCaseId caseId,
        SubmissionDraftProvenance provenance,
        ReadOnlyMemory<byte> body,
        string? subjectId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resumes the actor's draft for <paramref name="caseId"/>. Resolves the tenant +
    /// party fail-closed, then loads by the resolved key. Returns null when there is no
    /// resumable draft (or the subject was crypto-shredded).
    /// </summary>
    Task<SubmissionDraft?> ResumeDraftAsync(DraftCaseId caseId, CancellationToken ct = default);

    /// <summary>Abandons (deletes) the actor's draft for <paramref name="caseId"/>. Returns true when a draft was removed.</summary>
    Task<bool> AbandonDraftAsync(DraftCaseId caseId, CancellationToken ct = default);

    /// <summary>Lists the actor's resumable drafts in the active tenant (the "my in-progress cases" surface), most-recent first.</summary>
    Task<IReadOnlyList<SubmissionDraft>> ListMyDraftsAsync(CancellationToken ct = default);
}
