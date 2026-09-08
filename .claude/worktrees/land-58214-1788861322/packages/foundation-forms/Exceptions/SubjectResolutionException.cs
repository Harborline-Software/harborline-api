namespace Harborline.Api.Foundation.Forms.Exceptions;

/// <summary>
/// Raised when a field that <em>requires</em> a data subject (an
/// <c>Identifying</c>-classified / per-subject-keyed field) cannot resolve one
/// from its record's <see cref="Harborline.Api.Foundation.Forms.Models.RecordSubjectBinding"/>
/// — neither a per-field override nor a record primary subject is present
/// (ADR 0139 Amendment "PII at-rest key granularity: HYBRID").
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>fail-closed</b> outcome of
/// <see cref="Harborline.Api.Foundation.Forms.ISubjectResolver.ResolveRequired"/>. A
/// per-subject-keyed field whose subject is unknown MUST NOT silently fall back
/// to the tenant DEK: doing so would make the field un-crypto-shreddable and
/// silently re-pin it to a coarser key than its classification demands. Treating
/// the missing subject as a hard publish/store-time error is the point of the
/// seam — the consumer (the governance/PEP layer) lets this propagate so the
/// save is rejected rather than mis-keyed.
/// </para>
/// </remarks>
public sealed class SubjectResolutionException : Exception
{
    /// <summary>The JSON field name whose required subject could not be resolved.</summary>
    public string FieldName { get; }

    /// <summary>Constructs the exception for the given field.</summary>
    public SubjectResolutionException(string fieldName)
        : base($"Field '{fieldName}' requires a data subject (per-subject key) but none resolved "
            + "(no per-field override and no record primary subject). Failing closed — a per-subject-keyed "
            + "field must not fall back to the tenant key.")
    {
        FieldName = fieldName;
    }
}
