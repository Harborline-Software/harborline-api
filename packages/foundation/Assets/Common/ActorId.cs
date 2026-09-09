using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Assets.Common;

/// <summary>
/// Opaque identifier for an acting principal (user / service / system), in ONE canonical form.
/// </summary>
/// <remarks>
/// <para>
/// <b>The canonical form (ticket 274).</b> A canonical value has no leading or trailing whitespace, is
/// not empty, and is in Unicode normalization form C. The constructor REFUSES anything else, so a
/// whitespace-padded or decomposed spelling of a principal cannot exist as an <see cref="ActorId"/>
/// anywhere — at the wire (<see cref="ActorIdJsonConverter"/>), the roster delta, the grant store or the
/// audit append. Exactly one place canonicalises rather than refuses: <see cref="Mint"/>, used where an
/// id is first derived from a shell value (the genesis party derivation — ticket 296).
/// </para>
/// <para>
/// <b>Case.</b> The value is case-PRESERVING: the genesis record id is content-derived from these exact
/// bytes (Program.cs, #1291 F1), so folding case at the type would rewrite every stored id. Case is
/// instead handled at COMPARISON, per identity space. In the OS-user party space
/// <c>os:{user}#{key}</c> the <c>{user}</c> segment is an operating-system account name, which the OS
/// itself compares case-insensitively, so equality folds it; the <c>#{key}</c> suffix is an opaque node
/// key and every other id (<c>system</c>, <c>sunfish</c>, base64url principals, <c>installer:…</c>) is
/// opaque too, so those compare ordinally. Two spellings of one principal are therefore one
/// <see cref="ActorId"/>, or are refused at the boundary.
/// </para>
/// <para>
/// Two well-known sentinels:
/// <list type="bullet">
/// <item><description><see cref="System"/> — the system-internal actor used when no ambient context is available (e.g., scheduled jobs, background workers).</description></item>
/// <item><description><see cref="Harborline"/> — the Harborline-shipped Authoritative actor, used as the owner of compliance-source taxonomy definitions and other Authoritative-regime artifacts (per ADR 0056).</description></item>
/// </list>
/// </para>
/// </remarks>
[JsonConverter(typeof(ActorIdJsonConverter))]
public readonly record struct ActorId
{
    private const string OsPartyPrefix = "os:";

    /// <summary>Wraps an ALREADY canonical value; refuses anything else.</summary>
    /// <exception cref="ArgumentException"><paramref name="Value"/> is not in canonical form.</exception>
    public ActorId(string Value)
    {
        ArgumentNullException.ThrowIfNull(Value);
        if (!IsCanonical(Value))
            throw new ArgumentException(
                $"ActorId '{Value}' is not canonical (it must be non-empty, unpadded and Unicode form C). " +
                "Refuse it at the boundary, or mint it with ActorId.Mint if this is the derivation site.",
                nameof(Value));
        this.Value = Value;
    }

    /// <summary>The canonical value.</summary>
    public string Value { get; }

    /// <summary>True when <paramref name="value"/> is already in the canonical form.</summary>
    public static bool IsCanonical(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.AsSpan().Trim().Length == value.Length
        && value.IsNormalized(NormalizationForm.FormC);

    /// <summary>
    /// Canonicalises a raw value into an <see cref="ActorId"/>. The ONE minting site is the genesis
    /// party derivation from the shell (ticket 296); every other path refuses instead.
    /// </summary>
    public static ActorId Mint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ActorId(value.Trim().Normalize(NormalizationForm.FormC));
    }

    /// <summary>Do two raw strings name the same actor? For call sites holding un-typed identities.</summary>
    public static bool SameActor(string? left, string? right) =>
        string.Equals(ComparisonKey(left), ComparisonKey(right), StringComparison.Ordinal);

    /// <summary>The case-folded comparison key — see the case remark on the type.</summary>
    private static string? ComparisonKey(string? value)
    {
        if (value is null || !value.StartsWith(OsPartyPrefix, StringComparison.Ordinal))
            return value;
        var hash = value.IndexOf('#', StringComparison.Ordinal);
        var user = hash < 0 ? value[OsPartyPrefix.Length..] : value[OsPartyPrefix.Length..hash];
        return OsPartyPrefix + user.ToLowerInvariant() + (hash < 0 ? string.Empty : value[hash..]);
    }

    /// <inheritdoc />
    public bool Equals(ActorId other) => SameActor(Value, other.Value);

    /// <inheritdoc />
    public override int GetHashCode() =>
        ComparisonKey(Value) is { } key ? StringComparer.Ordinal.GetHashCode(key) : 0;

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string. Refuses a non-canonical value, like the constructor.</summary>
    public static implicit operator ActorId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(ActorId id) => id.Value;

    /// <summary>The system-internal actor used when no ambient context is available.</summary>
    public static ActorId System { get; } = new("system");

    /// <summary>The Harborline-shipped Authoritative actor — owner of compliance-source taxonomy definitions and other Authoritative-regime artifacts (per ADR 0056).</summary>
    public static ActorId Harborline { get; } = new("sunfish");
}

internal sealed class ActorIdJsonConverter : JsonConverter<ActorId>
{
    public override ActorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("ActorId must be a non-null string.");
        // The wire is a trust boundary: a non-canonical spelling is refused, never quietly canonicalised.
        if (!ActorId.IsCanonical(str))
            throw new JsonException($"ActorId '{str}' is not canonical (non-empty, unpadded, Unicode form C).");
        return new ActorId(str);
    }

    public override void Write(Utf8JsonWriter writer, ActorId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
