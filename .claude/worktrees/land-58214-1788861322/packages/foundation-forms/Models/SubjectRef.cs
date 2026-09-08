namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// A forms-layer reference to the <em>data subject</em> a field's value belongs
/// to — the unit at which per-subject crypto-shredding erasure is keyed
/// (ADR 0139 Amendment "PII at-rest key granularity: HYBRID"; ADR 0135 GDPR
/// direction; ADR 0118 D4 per-subject sub-keys).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The HYBRID at-rest key model pins <em>plain</em>
/// <c>Pii</c>/<c>Sensitive</c> fields to the tenant DEK (byte-identical to the
/// legacy path) but routes the <c>Identifying</c> element / tagged identifiers
/// to a <b>per-subject DEK</b> so a single data subject can be crypto-shredded
/// without touching anyone else. To know <em>whose</em> key to use, the
/// per-subject DEK path (<c>DeriveSubjectKeyAsync(tenant, subject)</c> in
/// Foundation.Recovery) must be told the field's data subject — this type is
/// what the resolution seam (<see cref="Harborline.Api.Foundation.Forms.ISubjectResolver"/>)
/// returns.
/// </para>
/// <para>
/// <b>Durable surrogate, never a mutable natural key.</b> The subject value is
/// mixed into the per-subject sub-key derivation's HKDF <c>info</c> prefix, so
/// it MUST be stable for the subject's lifetime: a changed subject id orphans
/// the ciphertext (it derives a different key and the old bytes no longer
/// decrypt). Therefore a <see cref="SubjectRef"/> must carry a <b>durable
/// surrogate</b> identifier (a record/party surrogate id, an opaque allocated
/// id), never a mutable natural identifier (email, phone, legal name). The
/// factory rejects a small denylist of well-known mutable natural-key schemes
/// (<see cref="ReservedNaturalKeySchemes"/>) as a guardrail — this is
/// defense-in-depth, NOT a proof of durability; the deployer remains
/// responsible for supplying a genuinely stable surrogate.
/// </para>
/// <para>
/// <b>Composition seam (no hard dependency on Foundation.Recovery).</b> Like
/// <see cref="IdentityRef"/>, the keystone treats this as an opaque value and
/// does NOT take a dependency on the crypto substrate. The consumer (the
/// governance/PEP layer that wires per-subject encryption at save time — a
/// later integration) maps a resolved <see cref="SubjectRef"/> to the
/// Foundation.Recovery <c>SubjectId</c> domain-separation label, typically via
/// the canonical <see cref="ToString"/> form. Keeping the mapping in the
/// consumer lets the keystone stay composable inside and outside an
/// authenticated/crypto-wired request scope (seed tooling, admin scripts).
/// </para>
/// <para>
/// <b>Reference type, deliberately.</b> Unlike the sibling <see cref="IdentityRef"/>
/// / <c>SubjectId</c> structs, this is a <see langword="sealed"/> <c>record</c>
/// (class) so the only "absent subject" is <see langword="null"/>. A
/// <c>default(struct)</c> empty value bypasses the factory's validation; for a
/// value that feeds key derivation, a silently-empty surrogate must not be
/// constructible — the fail-closed posture requires that every
/// <see cref="SubjectRef"/> in existence passed the factory guard.
/// </para>
/// </remarks>
public sealed record SubjectRef
{
    /// <summary>
    /// Well-known mutable natural-key schemes rejected by <see cref="Create"/>
    /// (case-insensitive). A <b>non-exhaustive</b> guardrail against the most
    /// common footguns — a durable surrogate id is the contract, not merely
    /// "any scheme not on this list".
    /// </summary>
    public static IReadOnlyCollection<string> ReservedNaturalKeySchemes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "email",
            "phone",
            "name",
            "fullname",
            "username",
            "ssn",
            "natural",
        };

    private SubjectRef(string scheme, string value)
    {
        Scheme = scheme;
        Value = value;
    }

    /// <summary>Surrogate scheme (for example <c>party</c>, <c>subject</c>,
    /// <c>surrogate</c>). Non-empty; contains no <c>':'</c>; not a reserved
    /// natural-key scheme.</summary>
    public string Scheme { get; }

    /// <summary>The durable surrogate identifier within the scheme. Non-empty.</summary>
    public string Value { get; }

    /// <summary>
    /// Constructs a subject reference, enforcing the durable-surrogate
    /// invariant.
    /// </summary>
    /// <param name="scheme">Surrogate scheme; trimmed, must be non-empty, must
    /// not contain <c>':'</c>, and must not be a
    /// <see cref="ReservedNaturalKeySchemes">reserved natural-key scheme</see>.</param>
    /// <param name="value">Durable surrogate identifier; trimmed, must be
    /// non-empty.</param>
    /// <exception cref="ArgumentException">Thrown when the scheme or value is
    /// null/empty/whitespace, the scheme contains <c>':'</c>, or the scheme is a
    /// reserved mutable natural-key scheme (the durable-surrogate guard).</exception>
    public static SubjectRef Create(string scheme, string value)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("SubjectRef scheme must be a non-empty surrogate scheme.", nameof(scheme));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SubjectRef value must be a non-empty durable surrogate id.", nameof(value));
        }

        var trimmedScheme = scheme.Trim();
        var trimmedValue = value.Trim();

        if (trimmedScheme.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"SubjectRef scheme must not contain ':' (it is the canonical 'scheme:value' separator); got '{trimmedScheme}'.",
                nameof(scheme));
        }

        if (ReservedNaturalKeySchemes.Contains(trimmedScheme))
        {
            throw new ArgumentException(
                $"SubjectRef scheme '{trimmedScheme}' is a mutable natural-key scheme; a data subject MUST be a durable surrogate id "
                + "(its value feeds per-subject key derivation, so a mutable identifier would orphan the ciphertext). "
                + "Supply a stable surrogate scheme (for example 'party' or 'subject').",
                nameof(scheme));
        }

        return new SubjectRef(trimmedScheme, trimmedValue);
    }

    /// <summary>
    /// Parses the canonical <c>scheme:value</c> form, splitting on the first
    /// <c>':'</c> (so the value may itself contain colons). Applies the same
    /// durable-surrogate guard as <see cref="Create"/>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the input is not
    /// <c>scheme:value</c> with non-empty segments.</exception>
    /// <exception cref="ArgumentException">Thrown when the parsed scheme is a
    /// reserved natural-key scheme.</exception>
    public static SubjectRef Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new FormatException($"SubjectRef expects 'scheme:value' with non-empty segments; got '{value}'.");
        }

        return Create(value[..colon], value[(colon + 1)..]);
    }

    /// <summary>
    /// Attempts to parse the canonical <c>scheme:value</c> form. Returns
    /// <see langword="false"/> (rather than throwing) for malformed input or a
    /// reserved natural-key scheme.
    /// </summary>
    public static bool TryParse(string? value, out SubjectRef? subject)
    {
        subject = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            return false;
        }

        try
        {
            subject = Create(value[..colon], value[(colon + 1)..]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Canonical wire form: <c>{Scheme}:{Value}</c>.</summary>
    public override string ToString() => $"{Scheme}:{Value}";
}
