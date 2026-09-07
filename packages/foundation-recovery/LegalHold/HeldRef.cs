using System;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The scope a <see cref="LegalHoldEntry"/> is placed against — the unit the
/// fail-closed pre-shred gate reasons over (ADR 0142 §D1). A hold covers either a
/// single data <em>subject</em> (the crypto-shred unit — the same identity the
/// per-subject key path uses, <see cref="SubjectId"/>), or a coarser
/// <em>record</em> or <em>data-class</em> scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opaque within Foundation.Recovery, deliberately.</b> The crypto-shred unit
/// here is <see cref="SubjectId"/> (the HKDF domain-separation label the erasure
/// path destroys). The forms-layer <c>SubjectRef</c> (ADR 0139 D3) maps <em>to</em>
/// a <see cref="SubjectId"/> in the consumer, exactly as the per-subject
/// encryption seam already does — this type stays a plain value so the recovery
/// substrate takes no dependency on the forms layer. A consumer that places a
/// hold from a resolved <c>SubjectRef</c> maps it to a <see cref="SubjectId"/>
/// (typically the canonical <c>scheme:value</c> form) and calls
/// <see cref="ForSubject"/>.
/// </para>
/// <para>
/// <b>Coverage is conservative (ADR 0142 §D3 / OQ1).</b> The erasure gate destroys
/// a <see cref="SubjectId"/>, so it consults the registry with the
/// <see cref="ForSubject"/> reference for that subject. A coarser record- or
/// class-scoped hold <em>encompasses</em> a subject only through a mapping the
/// recovery substrate does not own; the caller that knows the record→subject or
/// class→subject mapping supplies the covering references. Ambiguity always
/// resolves to held.
/// </para>
/// </remarks>
public readonly record struct HeldRef
{
    private HeldRef(HeldRefKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>What sort of scope this reference names.</summary>
    public HeldRefKind Kind { get; }

    /// <summary>
    /// The canonical value within <see cref="Kind"/> — a subject id, a
    /// <c>recordType/recordId</c> pair, or a data-class code. Never null/empty for
    /// a reference constructed through the factories.
    /// </summary>
    public string Value { get; }

    /// <summary>A hold covering a single data subject (the crypto-shred unit).</summary>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is the default (empty) value.</exception>
    public static HeldRef ForSubject(SubjectId subject)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
        {
            throw new ArgumentException("HeldRef.ForSubject requires a non-empty subject.", nameof(subject));
        }
        return new HeldRef(HeldRefKind.Subject, subject.Value);
    }

    /// <summary>A hold covering a specific record (a coarser scope than a subject).</summary>
    /// <exception cref="ArgumentException">Either segment is null/empty/whitespace or contains <c>'/'</c>.</exception>
    public static HeldRef ForRecord(string recordType, string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordType))
        {
            throw new ArgumentException("HeldRef.ForRecord requires a non-empty record type.", nameof(recordType));
        }
        if (string.IsNullOrWhiteSpace(recordId))
        {
            throw new ArgumentException("HeldRef.ForRecord requires a non-empty record id.", nameof(recordId));
        }
        var t = recordType.Trim();
        var id = recordId.Trim();
        if (t.Contains('/', StringComparison.Ordinal) || id.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("HeldRef.ForRecord segments must not contain '/' (the canonical separator).");
        }
        return new HeldRef(HeldRefKind.Record, $"{t}/{id}");
    }

    /// <summary>A hold covering an entire data class (the coarsest scope).</summary>
    /// <exception cref="ArgumentException"><paramref name="dataClass"/> is null/empty/whitespace.</exception>
    public static HeldRef ForClass(string dataClass)
    {
        if (string.IsNullOrWhiteSpace(dataClass))
        {
            throw new ArgumentException("HeldRef.ForClass requires a non-empty data class.", nameof(dataClass));
        }
        return new HeldRef(HeldRefKind.Class, dataClass.Trim());
    }

    /// <summary>
    /// Reconstruct a reference from its already-validated persisted components (the
    /// durable store's load path). Bypasses the factory split-validation because the
    /// value passed the factory guard when it was first written.
    /// </summary>
    internal static HeldRef Rehydrate(HeldRefKind kind, string value) => new(kind, value);

    /// <summary>The canonical, stable key form: <c>{Kind}:{Value}</c> (used for storage keying).</summary>
    public string Canonical => $"{Kind}:{Value}";

    /// <inheritdoc />
    public override string ToString() => Canonical;
}

/// <summary>The kind of scope a <see cref="HeldRef"/> names (ADR 0142 §D1).</summary>
public enum HeldRefKind
{
    /// <summary>A single data subject — the crypto-shred unit (<see cref="SubjectId"/>).</summary>
    Subject,

    /// <summary>A specific record, identified by <c>recordType/recordId</c>.</summary>
    Record,

    /// <summary>An entire data class.</summary>
    Class,
}
