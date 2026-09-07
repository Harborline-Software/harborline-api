namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The data-subject projection of a form <em>instance</em> (a filled-out
/// record) — the input the <see cref="Harborline.Api.Foundation.Forms.ISubjectResolver"/>
/// reads to decide whose per-subject key a field is encrypted under
/// (ADR 0139 Amendment "PII at-rest key granularity: HYBRID"; the DP-2
/// subject-resolution model).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate holder (not the FormDefinition).</b> The data subject is
/// <em>instance</em> data — "this particular record is about person X" — not
/// <em>definition</em> data. The keystone (<see cref="FormDefinition"/> +
/// <see cref="HarborlineOverlay"/>) describes the form template and has no
/// instance type; the entity/record store owns instances. So the binding is a
/// small, self-contained projection that the consumer (the governance/PEP
/// layer wiring per-subject encryption at save time — a later integration)
/// builds from the instance and hands to the resolver. The keystone keeps the
/// seam abstract: it never reaches into a concrete record shape.
/// </para>
/// <para>
/// <b>Shape — primary subject plus per-field overrides.</b>
/// </para>
/// <list type="number">
/// <item><description><see cref="PrimarySubject"/> — the single natural person
///   the record is <em>about</em> (the applicant, the tenant-of-record, the
///   vendor contact). It is the default subject for every subject-scoped field
///   on the record. <see langword="null"/> ⇒ the record names no primary
///   subject.</description></item>
/// <item><description><see cref="FieldOverrides"/> — for multi-subject records
///   (a co-applicant form, a household roster) where a given field is about a
///   <em>different</em> person than the record's primary subject. Keyed by JSON
///   field name (matching <see cref="HarborlineOverlay.Fields"/> /
///   <see cref="FormSection.Fields"/>). An entry always names a concrete
///   subject — there is no "override to none".</description></item>
/// </list>
/// <para>
/// The resolution precedence (override ▸ primary ▸ none) lives in
/// <see cref="Harborline.Api.Foundation.Forms.ISubjectResolver"/>; this record carries
/// only the data.
/// </para>
/// </remarks>
/// <param name="PrimarySubject">The record's primary data subject, or
/// <see langword="null"/> when the record names none.</param>
/// <param name="FieldOverrides">Optional per-field subject overrides keyed by
/// JSON field name, for fields whose subject differs from
/// <paramref name="PrimarySubject"/>. <see langword="null"/> or empty ⇒ no
/// overrides.</param>
public sealed record RecordSubjectBinding(
    SubjectRef? PrimarySubject = null,
    IReadOnlyDictionary<string, SubjectRef>? FieldOverrides = null)
{
    /// <summary>
    /// A binding that names no subject at all — no primary, no overrides. A
    /// subject-scoped field resolved against this yields "none"; a
    /// <em>required</em>-subject field resolved against it fails closed.
    /// </summary>
    public static RecordSubjectBinding None { get; } = new();

    /// <summary>
    /// Returns a copy of this binding with a per-field override added (or
    /// replaced) for <paramref name="fieldName"/>. Convenience for building a
    /// multi-subject binding; the keystone never mutates a binding in place.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when
    /// <paramref name="fieldName"/> is null/empty/whitespace.</exception>
    public RecordSubjectBinding WithFieldOverride(string fieldName, SubjectRef subject)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("Field name must be non-empty.", nameof(fieldName));
        }

        ArgumentNullException.ThrowIfNull(subject);

        var next = FieldOverrides is null
            ? new Dictionary<string, SubjectRef>(StringComparer.Ordinal)
            : new Dictionary<string, SubjectRef>(FieldOverrides, StringComparer.Ordinal);
        next[fieldName] = subject;
        return this with { FieldOverrides = next };
    }
}
