using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Definitions;

/// <summary>
/// The control metadata carried by a configuration definition independently of its body.
/// </summary>
/// <typeparam name="TIdentity">The definition's typed identity.</typeparam>
/// <typeparam name="TVersion">The definition's typed version.</typeparam>
/// <typeparam name="TTenant">The definition's typed tenant scope.</typeparam>
/// <typeparam name="TProvenance">The definition-specific provenance and lineage.</typeparam>
public sealed record DefinitionEnvelope<TIdentity, TVersion, TTenant, TProvenance>
{
    /// <summary>Constructs authored and transported definition coordinates.</summary>
    [JsonConstructor]
    public DefinitionEnvelope(
        TIdentity Identity,
        TVersion Version,
        TTenant Tenant,
        CascadeLayer CascadeLayer,
        TProvenance Provenance,
        IReadOnlyList<DefinitionRequirement> Requires)
    {
        this.Identity = Identity;
        this.Version = Version;
        this.Tenant = Tenant;
        this.CascadeLayer = CascadeLayer;
        this.Provenance = Provenance;
        this.Requires = Requires ?? throw new ArgumentNullException(nameof(Requires));
    }

    /// <summary>
    /// Rejects an independently-authored retention value. Retention is resolved through its registry.
    /// </summary>
    /// <exception cref="DefinitionPolicyAuthorityBypassException">
    /// Always thrown because authored envelopes cannot carry registry-owned values.
    /// </exception>
    public DefinitionEnvelope(
        TIdentity Identity,
        TVersion Version,
        TTenant Tenant,
        CascadeLayer CascadeLayer,
        TProvenance Provenance,
        DefinitionRetentionClass RetentionClass,
        IReadOnlyList<DefinitionRequirement> Requires)
        : this(Identity, Version, Tenant, CascadeLayer, Provenance, Requires)
        => throw new DefinitionPolicyAuthorityBypassException("definition.retention.registry_bypass");

    /// <summary>
    /// Rejects an independently-authored legal-hold value. Legal hold is resolved through its registry.
    /// </summary>
    /// <exception cref="DefinitionPolicyAuthorityBypassException">
    /// Always thrown because authored envelopes cannot carry registry-owned values.
    /// </exception>
    public DefinitionEnvelope(
        TIdentity Identity,
        TVersion Version,
        TTenant Tenant,
        CascadeLayer CascadeLayer,
        TProvenance Provenance,
        DefinitionLegalHold LegalHold,
        IReadOnlyList<DefinitionRequirement> Requires)
        : this(Identity, Version, Tenant, CascadeLayer, Provenance, Requires)
        => throw new DefinitionPolicyAuthorityBypassException("definition.legal_hold.registry_bypass");

    /// <summary>The definition identity.</summary>
    public TIdentity Identity { get; init; }

    /// <summary>The definition version.</summary>
    public TVersion Version { get; init; }

    /// <summary>The tenant scope.</summary>
    public TTenant Tenant { get; init; }

    /// <summary>The definition's place in the configuration cascade.</summary>
    public CascadeLayer CascadeLayer { get; init; }

    /// <summary>Authorship and lineage provenance.</summary>
    public TProvenance Provenance { get; init; }

    /// <summary>Platform capabilities required by this definition.</summary>
    public IReadOnlyList<DefinitionRequirement> Requires { get; init; }
}

/// <summary>The named failure raised when authored data tries to bypass a definition policy registry.</summary>
public sealed class DefinitionPolicyAuthorityBypassException : InvalidOperationException
{
    /// <summary>Constructs the failure with its stable machine-readable error code.</summary>
    public DefinitionPolicyAuthorityBypassException(string errorCode)
        : base($"Definition policy authority bypass refused: {errorCode}.")
        => ErrorCode = errorCode;

    /// <summary>The stable machine-readable error code.</summary>
    public string ErrorCode { get; }
}

/// <summary>A legacy unresolved retention value accepted only by the rejecting compatibility constructor.</summary>
/// <param name="Value">The stable retention-class token.</param>
public readonly record struct DefinitionRetentionClass(string Value)
{
    /// <summary>The compatibility value used when an existing definition has no classification yet.</summary>
    public static DefinitionRetentionClass Unspecified { get; } = new("unspecified");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A registry-resolved legal-hold state exposed only by a resolved definition envelope.</summary>
public enum DefinitionLegalHold
{
    /// <summary>No legal hold is recorded.</summary>
    NotHeld = 0,

    /// <summary>A legal hold is recorded.</summary>
    Held = 1,
}

/// <summary>A capability requirement declared by a definition.</summary>
/// <param name="Capability">The stable capability name.</param>
/// <param name="MinimumPlatformVersion">Optional platform-version floor used as a backstop.</param>
public sealed record DefinitionRequirement(
    string Capability,
    string? MinimumPlatformVersion = null);
