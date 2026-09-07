using System;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// A save-and-resume <b>submission-draft</b> (ADR 0135 amendment 2026-07-01 —
/// D2). The mutable, pre-submission working state of a form the actor is filling
/// in, keyed by <see cref="SubmissionDraftKey"/> so it resumes on any device the
/// same (tenant, case, party) reaches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Draft, not submission.</b> Unlike the immutable D3
/// <c>ImmutableSubmissionPayload</c> (final values + append-only Mint), a draft is
/// deliberately partial and re-writable: it carries the actor's in-progress values
/// (<see cref="Body"/>), a provenance header that pins the schema / definition /
/// engine / locale in force (so a resumed draft binds to the same contract), and
/// <see cref="CreatedAt"/> / <see cref="UpdatedAt"/> — never a <c>submittedAt</c>.
/// Promoting a draft to a real submission is the D3 <c>SaveAsync</c> path; the draft
/// is then abandoned.
/// </para>
/// <para>
/// <b>Governed, not an ungoverned device blob.</b> An optional
/// <see cref="SubjectId"/> names the data subject whose PII the draft holds, so the
/// persistence layer can (a) make the draft visible to the legal-hold registry
/// (ADR 0142), (b) classify it for retention (ADR 0137), and (c) render it
/// crypto-shreddable on subject erasure (ADR 0135/0139). <see cref="ExpiresAt"/> is
/// the retention TTL a purge sweep honours (legal-hold-gated).
/// </para>
/// </remarks>
/// <param name="Key">The keying tuple (tenant, case, party).</param>
/// <param name="FormId">The form definition this draft is being filled against.</param>
/// <param name="Provenance">The schema / definition / engine / locale binding in force when saved.</param>
/// <param name="Body">The serialized partial candidate values (UTF-8 JSON object bytes).</param>
/// <param name="SubjectId">
/// The opaque data subject whose PII the draft holds (for legal-hold / retention /
/// crypto-shred visibility). Null when the draft names no subject.
/// </param>
/// <param name="CreatedAt">When the draft was first saved.</param>
/// <param name="UpdatedAt">When the draft was last saved.</param>
/// <param name="ExpiresAt">The retention TTL after which a legal-hold-gated purge may remove it; null = no TTL.</param>
public sealed record SubmissionDraft(
    SubmissionDraftKey Key,
    FormDefinitionId FormId,
    SubmissionDraftProvenance Provenance,
    ReadOnlyMemory<byte> Body,
    string? SubjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// The near-free provenance header stamped on a draft — the "under which schema /
/// definition / engine / locale was this being filled" record. Mirrors the D3
/// <c>SubmissionBindingHeader</c> shape so a resumed draft binds to the exact same
/// contract, but carries <em>save</em> timing rather than a final submit instant.
/// </summary>
/// <param name="SchemaRef">Content-addressed CID of the JSON Schema the draft validates against.</param>
/// <param name="DefinitionId">The form definition id.</param>
/// <param name="DefinitionVersion">Canonical <c>"{major}.{minor}.{patch}"</c> version of the definition revision.</param>
/// <param name="EngineVersion">The rule/compute engine identifier in force (e.g. <c>"shipyard-jsonlogic/v1"</c>).</param>
/// <param name="LocaleChain">The ordered actor locale-preference chain (RFC 5646 tags) in force at save time.</param>
public sealed record SubmissionDraftProvenance(
    string SchemaRef,
    string DefinitionId,
    string DefinitionVersion,
    string EngineVersion,
    IReadOnlyList<string> LocaleChain)
{
    /// <summary>The canonical SPINE-1 rule/compute engine identifier (ADR 0140 D1).</summary>
    public const string HarborlineJsonLogicV1 = "shipyard-jsonlogic/v1";

    /// <summary>Builds a provenance header from the resolved definition + the actor locale chain.</summary>
    public static SubmissionDraftProvenance Create(
        FormDefinition definition,
        IReadOnlyList<string> localeChain,
        string engineVersion = HarborlineJsonLogicV1)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(localeChain);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);

        return new SubmissionDraftProvenance(
            SchemaRef: definition.SchemaRef.Value,
            DefinitionId: definition.Id.Value,
            DefinitionVersion: definition.Version.ToString(),
            EngineVersion: engineVersion,
            // Defensive copy → immutable against later mutation of the caller's list.
            LocaleChain: localeChain.ToArray());
    }
}
