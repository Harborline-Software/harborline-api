using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// Reference <see cref="ISubmissionDraftService"/> (ADR 0135 amendment 2026-07-01 — D2).
/// Composes the fail-closed <see cref="IPartyContext"/>, the ambient tenant, an
/// <see cref="ISubmissionDraftStore"/>, and a clock to key every draft by
/// <c>(TenantId, case/subject id, PartyId)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed keying is the whole point.</b> <see cref="ResolveScopeAsync"/> calls
/// <c>IPartyContext.GetCurrentPartyIdAsync</c> FIRST. That throws
/// <c>PrincipalPartyResolutionException</c> when the principal maps to no party — so a
/// mis-resolved actor BLOCKS here, before any key is formed. The tenant is then read off
/// the SAME ambient <see cref="ITenantContext"/> principal instance the party was derived
/// from (same-token derivation), so a UserId and a TenantId can never come from two
/// different principals, and no draft is ever mis-keyed across tenants.
/// </para>
/// </remarks>
public sealed class SubmissionDraftService : ISubmissionDraftService
{
    private readonly IPartyContext _party;
    private readonly ITenantContext _principal;
    private readonly ISubmissionDraftStore _store;
    private readonly TimeProvider _clock;

    /// <summary>Constructs the service over the party seam, the ambient principal, the store, and a clock.</summary>
    public SubmissionDraftService(
        IPartyContext party,
        ITenantContext principal,
        ISubmissionDraftStore store,
        TimeProvider? clock = null)
    {
        _party = party ?? throw new ArgumentNullException(nameof(party));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<SubmissionDraftKey> SaveDraftAsync(
        FormDefinitionId formId,
        DraftCaseId caseId,
        SubmissionDraftProvenance provenance,
        ReadOnlyMemory<byte> body,
        string? subjectId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        var (tenant, partyId) = await ResolveScopeAsync(ct).ConfigureAwait(false);
        var key = new SubmissionDraftKey(tenant, caseId, partyId);
        var now = _clock.GetUtcNow();

        // Preserve the original CreatedAt across re-saves (same key = the same draft).
        var existing = await _store.GetAsync(key, ct).ConfigureAwait(false);
        var createdAt = existing?.CreatedAt ?? now;

        var draft = new SubmissionDraft(
            Key: key,
            FormId: formId,
            Provenance: provenance,
            Body: body,
            SubjectId: subjectId,
            CreatedAt: createdAt,
            UpdatedAt: now,
            ExpiresAt: expiresAt);

        await _store.UpsertAsync(draft, ct).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task<SubmissionDraft?> ResumeDraftAsync(DraftCaseId caseId, CancellationToken ct = default)
    {
        var (tenant, partyId) = await ResolveScopeAsync(ct).ConfigureAwait(false);
        var key = new SubmissionDraftKey(tenant, caseId, partyId);
        return await _store.GetAsync(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AbandonDraftAsync(DraftCaseId caseId, CancellationToken ct = default)
    {
        var (tenant, partyId) = await ResolveScopeAsync(ct).ConfigureAwait(false);
        var key = new SubmissionDraftKey(tenant, caseId, partyId);
        return await _store.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubmissionDraft>> ListMyDraftsAsync(CancellationToken ct = default)
    {
        var (tenant, partyId) = await ResolveScopeAsync(ct).ConfigureAwait(false);
        return await _store.ListByPartyAsync(tenant, partyId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the <c>(TenantId, PartyId)</c> key scope from the ambient principal,
    /// FAIL-CLOSED. The party is resolved first (throws on a mis-provisioned principal);
    /// the tenant is then read off the same principal instance the party was derived from.
    /// </summary>
    private async Task<(TenantId Tenant, Guid PartyId)> ResolveScopeAsync(CancellationToken ct)
    {
        // Fail-closed: throws PrincipalPartyResolutionException when the principal maps to
        // no party (or none is present). We never fall through to Guid.Empty.
        var partyId = await _party.GetCurrentPartyIdAsync(ct).ConfigureAwait(false);

        // Read the tenant off the SAME ambient principal the party was derived from. A
        // successful party resolution guarantees a present, tenant-resolved principal; the
        // null guard fails closed on the (unreachable-by-construction) inconsistent case.
        var tenant = _principal.Tenant;
        if (tenant is null)
        {
            throw PrincipalPartyResolutionException.NoAuthenticatedPrincipal();
        }

        return (tenant.Id, partyId);
    }
}
