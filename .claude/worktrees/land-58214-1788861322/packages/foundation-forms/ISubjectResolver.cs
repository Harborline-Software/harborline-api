using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Resolves the effective data subject for a field on a form instance — the
/// seam the per-subject DEK path consults to know <em>whose</em> key to encrypt
/// a field under (ADR 0139 Amendment "PII at-rest key granularity: HYBRID"; the
/// DP-2 subject-resolution model).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this sits.</b> Under the HYBRID key model, plain
/// <c>Pii</c>/<c>Sensitive</c> fields are tenant-keyed (no subject needed) while
/// the <c>Identifying</c> element / tagged identifiers are <b>per-subject</b>
/// keyed so a single subject can be crypto-shredded. When a field is
/// subject-scoped, the encryption path needs a stable
/// <see cref="SubjectRef"/>; this resolver computes it from the record's
/// <see cref="RecordSubjectBinding"/>.
/// </para>
/// <para>
/// <b>Precedence (override ▸ primary ▸ none).</b>
/// </para>
/// <list type="number">
/// <item><description>A per-field override on the binding (multi-subject
///   records) wins.</description></item>
/// <item><description>Otherwise the record's primary subject applies.</description></item>
/// <item><description>Otherwise there is no subject ("none").</description></item>
/// </list>
/// <para>
/// <b>Classification-agnostic by design.</b> This seam does <em>not</em> decide
/// which fields are subject-scoped (that is the data-class / aspect taxonomy the
/// governance layer owns). It exposes two operations: <see cref="Resolve"/>
/// (returns "none" as <see langword="null"/>) for callers that tolerate no
/// subject, and <see cref="ResolveRequired"/> (fail-closed) for a field the
/// caller has already determined <em>must</em> be per-subject keyed. The
/// governance/PEP layer picks the right one per field; the keystone provides
/// the mechanism, not the policy.
/// </para>
/// </remarks>
public interface ISubjectResolver
{
    /// <summary>
    /// Resolves the effective data subject for <paramref name="fieldName"/> on a
    /// record with the given <paramref name="binding"/>, applying the
    /// override ▸ primary ▸ none precedence.
    /// </summary>
    /// <param name="binding">The record's data-subject projection.</param>
    /// <param name="fieldName">JSON field name (matching
    /// <see cref="HarborlineOverlay.Fields"/> / <see cref="FormSection.Fields"/>).</param>
    /// <returns>The effective <see cref="SubjectRef"/>, or <see langword="null"/>
    /// when the record names no subject for this field.</returns>
    SubjectRef? Resolve(RecordSubjectBinding binding, string fieldName);

    /// <summary>
    /// Resolves the effective data subject for a field that <em>requires</em>
    /// one (an <c>Identifying</c>-classified / per-subject-keyed field),
    /// <b>failing closed</b>: throws <see cref="SubjectResolutionException"/>
    /// when no subject resolves rather than returning <see langword="null"/> or
    /// falling back to a tenant-scoped key.
    /// </summary>
    /// <param name="binding">The record's data-subject projection.</param>
    /// <param name="fieldName">JSON field name.</param>
    /// <returns>The effective <see cref="SubjectRef"/> (never <see langword="null"/>).</returns>
    /// <exception cref="SubjectResolutionException">Thrown when neither a
    /// per-field override nor a record primary subject is present.</exception>
    SubjectRef ResolveRequired(RecordSubjectBinding binding, string fieldName);
}
