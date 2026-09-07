namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// Caveat names written by <see cref="MacaroonFormCapabilityIssuer"/> and
/// consumed by <see cref="MacaroonFormCapabilityVerifier"/>. Centralized to
/// eliminate issuer/verifier string-drift — the same risk the public-listings
/// capability cluster addressed by centralizing its caveat names.
/// </summary>
internal static class FormCapabilityCaveatNames
{
    /// <summary>Tenant binding (single). The macaroon-bound active tenant — INV-S1 anchor.</summary>
    public const string Tenant = "tenant";

    /// <summary>Acting principal (single).</summary>
    public const string Subject = "subject";

    /// <summary>A granted role (repeatable; zero or more).</summary>
    public const string Role = "role";

    /// <summary>A granted coarse action — <c>read</c> or <c>write</c> (repeatable).</summary>
    public const string Action = "action";

    /// <summary>Absolute expiry, ISO-8601 round-trip ("O") format (single).</summary>
    public const string Expires = "expires";
}
