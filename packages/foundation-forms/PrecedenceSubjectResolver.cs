using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Default <see cref="ISubjectResolver"/> — applies the
/// override ▸ primary ▸ none precedence over a record's
/// <see cref="RecordSubjectBinding"/> (ADR 0139 Amendment "PII at-rest key
/// granularity: HYBRID").
/// </summary>
/// <remarks>
/// Stateless and side-effect-free, so it registers as a singleton
/// (<c>AddDefaultSubjectResolver</c> /
/// <c>TryAddDefaultSubjectResolver</c>). The keystone ships this reference
/// implementation; a host may override with a richer resolver (for example one
/// that computes a surrogate from a record's natural key via a deterministic
/// pseudonymisation) by registering its own <see cref="ISubjectResolver"/>.
/// </remarks>
public sealed class PrecedenceSubjectResolver : ISubjectResolver
{
    /// <inheritdoc />
    public SubjectRef? Resolve(RecordSubjectBinding binding, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("Field name must be non-empty.", nameof(fieldName));
        }

        // 1. Per-field override wins (multi-subject records).
        if (binding.FieldOverrides is { Count: > 0 } overrides
            && overrides.TryGetValue(fieldName, out var overridden))
        {
            return overridden;
        }

        // 2. Otherwise the record's primary subject (may itself be null = none).
        // 3. null ⇒ none.
        return binding.PrimarySubject;
    }

    /// <inheritdoc />
    public SubjectRef ResolveRequired(RecordSubjectBinding binding, string fieldName)
        => Resolve(binding, fieldName)
            ?? throw new SubjectResolutionException(fieldName);
}
