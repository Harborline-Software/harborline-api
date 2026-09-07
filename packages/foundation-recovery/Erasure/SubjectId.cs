using System;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Opaque identifier for a GDPR <em>data subject</em> within a tenant — the unit
/// of crypto-shredding erasure (ADR 0135 GDPR direction). A subject is whatever
/// natural person the deployer maps to a right-to-erasure request (an applicant,
/// a tenant-of-record, a vendor contact, etc.); the crypto layer treats it as an
/// opaque domain-separation label, never parsing it.
/// </summary>
/// <remarks>
/// The value is mixed into the per-subject sub-key derivation's HKDF
/// <c>info</c> prefix, so two distinct subjects in the same tenant derive
/// independent sub-keys and shredding one cannot affect the other. Because the
/// value participates in key derivation, it MUST be stable for the subject's
/// lifetime — choose a durable surrogate key, not a mutable natural identifier.
/// </remarks>
public readonly record struct SubjectId
{
    /// <summary>The opaque, non-empty subject value.</summary>
    public string Value { get; }

    /// <summary>Construct a subject id. The value must be non-empty.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is null/empty/whitespace.</exception>
    public SubjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SubjectId must be a non-empty value.", nameof(value));
        }
        Value = value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
