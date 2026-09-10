namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Stable reference to a principal (party / user / system actor) for
/// authorship metadata on a <see cref="FormDefinition"/> (ADR 0055).
/// </summary>
/// <remarks>
/// <para>
/// Held by value on schema metadata (<see cref="FormDefinition.Owner"/>,
/// authorship audit). The schema substrate treats the reference as opaque;
/// resolution to a concrete <c>IPartyContext</c> (the ADR 0102 substrate that
/// earlier repository ticket #216 landed) is the consumer's responsibility — the keystone does
/// not take a hard dependency on the party context substrate because the
/// keystone must remain composable both inside and outside an authenticated
/// request scope (admin authoring scripts, seed data tooling, etc.).
/// </para>
/// </remarks>
/// <param name="Scheme">Identity scheme (for example <c>user</c>, <c>system</c>,
/// <c>tenant-admin</c>, <c>party</c>). MUST be non-empty.</param>
/// <param name="Value">Opaque identifier within the scheme. MUST be non-empty.</param>
public readonly record struct IdentityRef(string Scheme, string Value)
{
    /// <summary>Canonical wire form: <c>{Scheme}:{Value}</c>.</summary>
    public override string ToString() => $"{Scheme}:{Value}";

    /// <summary>
    /// Parses the canonical string form. Requires exactly one ':' separator
    /// with non-empty segments on either side.
    /// </summary>
    public static IdentityRef Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new FormatException($"IdentityRef expects 'scheme:value' with non-empty segments; got '{value}'.");
        }

        return new IdentityRef(value[..colon], value[(colon + 1)..]);
    }

    /// <summary>
    /// Sentinel for system-authored schemas (seed data, migrations,
    /// background jobs). Mirrors the <c>__system</c> reserved-prefix
    /// convention used elsewhere in the foundation tier.
    /// </summary>
    public static IdentityRef System { get; } = new("system", "__sunfish");
}
