using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Drafts;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Durable, restart-surviving <see cref="ISubmissionDraftStore"/> over the SQLCipher-encrypted
/// <see cref="NodeLocalDraftsDbContext"/> (ADR 0135 amendment 2026-07-01 — D2). Composes the
/// ADR-0139 subject-erasure registry (crypto-shred visibility) and the ADR-0142 legal-hold
/// registry (retention-purge gate).
/// </summary>
/// <remarks>
/// <para>
/// <b>At-rest.</b> The body is stored inside the node's SQLCipher whole-file-encrypted DB
/// (SC-1) — the node's standard at-rest posture. Per-subject-DEK body encryption is an
/// additive follow-up gated on the ADR-0139 <c>subjectRef</c> primary-subject seam (currently
/// fail-closed-null on the node).
/// </para>
/// <para>
/// <b>Crypto-shred (subject erasure).</b> A draft carrying a <see cref="SubmissionDraft.SubjectId"/>
/// is checked against <see cref="ISubjectErasureRegistry.IsErasedAsync"/> on read: an erased
/// subject's draft is hard-deleted and returned as ABSENT. This is the documented
/// envelope-scheme equivalent of destroying a wrapped key — the durable mutation layer that
/// owns the blob deletes it under the recorded erasure decision (<see cref="ISubjectErasureRegistry"/>
/// remarks) — so an erased subject's in-progress PII is unrecoverable, surviving restart.
/// </para>
/// <para>
/// <b>Legal-hold + retention.</b> <see cref="PurgeExpiredAsync"/> only removes an expired
/// draft when its subject is NOT under an active legal hold
/// (<see cref="ILegalHoldRegistry.IsHeld"/>) — the ADR-0142 hold-gate on the ADR-0137
/// retention lifecycle. A draft is thus a GOVERNED record visible to both registries, never
/// an ungoverned device blob.
/// </para>
/// </remarks>
public sealed class NodeEfSubmissionDraftStore : ISubmissionDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalDraftsDbContext> _contextFactory;
    private readonly ISubjectErasureRegistry _erasure;
    private readonly ILegalHoldRegistry _legalHold;

    /// <summary>Constructs the store over the context factory + the erasure / legal-hold registries.</summary>
    public NodeEfSubmissionDraftStore(
        IDbContextFactory<NodeLocalDraftsDbContext> contextFactory,
        ISubjectErasureRegistry erasure,
        ILegalHoldRegistry legalHold)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));
        _legalHold = legalHold ?? throw new ArgumentNullException(nameof(legalHold));
    }

    /// <inheritdoc />
    public async Task UpsertAsync(SubmissionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var (tenant, caseId, party) = KeyParts(draft.Key);
        var row = await ctx.Drafts.FindAsync(new object[] { tenant, caseId, party }, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new SubmissionDraftRow { TenantId = tenant, CaseId = caseId, PartyId = party };
            ctx.Drafts.Add(row);
        }

        row.FormId = draft.FormId.Value;
        row.SchemaRef = draft.Provenance.SchemaRef;
        row.DefinitionId = draft.Provenance.DefinitionId;
        row.DefinitionVersion = draft.Provenance.DefinitionVersion;
        row.EngineVersion = draft.Provenance.EngineVersion;
        row.LocaleChainJson = JsonSerializer.Serialize(draft.Provenance.LocaleChain, JsonOptions);
        row.Body = draft.Body.ToArray();
        row.SubjectId = draft.SubjectId;
        row.CreatedAtUtc = draft.CreatedAt;
        row.UpdatedAtUtc = draft.UpdatedAt;
        row.ExpiresAtUtc = draft.ExpiresAt;

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubmissionDraft?> GetAsync(SubmissionDraftKey key, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var (tenant, caseId, party) = KeyParts(key);
        var row = await ctx.Drafts.FindAsync(new object[] { tenant, caseId, party }, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        // Crypto-shred: an erased subject's draft is deleted and read as absent.
        if (await IsShreddedAsync(row, ct).ConfigureAwait(false))
        {
            ctx.Drafts.Remove(row);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }

        return Map(row, key);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(SubmissionDraftKey key, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var (tenant, caseId, party) = KeyParts(key);
        var row = await ctx.Drafts.FindAsync(new object[] { tenant, caseId, party }, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        ctx.Drafts.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubmissionDraft>> ListByPartyAsync(TenantId tenant, Guid partyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var partyKey = partyId.ToString("N");
        // SQLite cannot ORDER BY a DateTimeOffset column server-side — filter in the DB, order in memory.
        var rows = (await ctx.Drafts
                .Where(r => r.TenantId == tenant.Value && r.PartyId == partyKey)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .OrderByDescending(r => r.UpdatedAtUtc)
            .ToList();

        var drafts = new List<SubmissionDraft>(rows.Count);
        foreach (var row in rows)
        {
            // A crypto-shredded subject's draft is never surfaced (and is swept from the list).
            if (await IsShreddedAsync(row, ct).ConfigureAwait(false))
            {
                continue;
            }
            drafts.Add(Map(row, KeyFrom(row)));
        }
        return drafts;
    }

    /// <summary>
    /// Purges every draft whose retention TTL has lapsed as of <paramref name="asOf"/> AND whose
    /// subject is NOT under an active legal hold. Returns the count removed. This is the ADR-0137
    /// retention sweep gated by the ADR-0142 legal-hold registry — a held draft is retained even
    /// past its TTL (legal-hold visibility). A subjectless draft has no hold to consult and is
    /// purged on TTL.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // SQLite cannot compare a DateTimeOffset column server-side — fetch TTL-bearing rows, compare in memory.
        var expired = (await ctx.Drafts
                .Where(r => r.ExpiresAtUtc != null)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Where(r => r.ExpiresAtUtc <= asOf)
            .ToList();

        var purged = 0;
        foreach (var row in expired)
        {
            if (row.SubjectId is { } subject
                && _legalHold.IsHeld(new TenantId(row.TenantId), new SubjectId(subject)))
            {
                // Under legal hold — retained past its TTL (the ADR-0142 hold-gate).
                continue;
            }
            ctx.Drafts.Remove(row);
            purged++;
        }

        if (purged > 0)
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return purged;
    }

    /// <summary>True when the row names a subject that has been crypto-shredded for its tenant.</summary>
    private async Task<bool> IsShreddedAsync(SubmissionDraftRow row, CancellationToken ct)
    {
        if (row.SubjectId is not { } subject)
        {
            return false;
        }
        return await _erasure
            .IsErasedAsync(new TenantId(row.TenantId), new SubjectId(subject), ct)
            .ConfigureAwait(false);
    }

    private static (string Tenant, string Case, string Party) KeyParts(SubmissionDraftKey key) =>
        (key.Tenant.Value, key.Case.Value, key.PartyId.ToString("N"));

    private static SubmissionDraftKey KeyFrom(SubmissionDraftRow row) => new(
        new TenantId(row.TenantId),
        DraftCaseId.Create(row.CaseId),
        Guid.ParseExact(row.PartyId, "N"));

    private static SubmissionDraft Map(SubmissionDraftRow row, SubmissionDraftKey key)
    {
        var localeChain = JsonSerializer.Deserialize<string[]>(row.LocaleChainJson, JsonOptions)
            ?? Array.Empty<string>();
        var provenance = new SubmissionDraftProvenance(
            row.SchemaRef, row.DefinitionId, row.DefinitionVersion, row.EngineVersion, localeChain);
        return new SubmissionDraft(
            Key: key,
            FormId: new FormDefinitionId(row.FormId),
            Provenance: provenance,
            Body: row.Body,
            SubjectId: row.SubjectId,
            CreatedAt: row.CreatedAtUtc,
            UpdatedAt: row.UpdatedAtUtc,
            ExpiresAt: row.ExpiresAtUtc);
    }
}
