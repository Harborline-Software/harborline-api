using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// An unforgeable, already-verified form capability. The constructor is
/// <see langword="internal"/>: a <see cref="CapabilityToken"/> can only be
/// minted by <see cref="MacaroonFormCapabilityVerifier"/> after a macaroon's
/// signature chain, tenant binding, action grant, and expiry have all been
/// checked. Possessing an instance therefore <i>is</i> the proof of a verified
/// capability — <see cref="IFormEngine"/> trusts the value object and does not
/// re-verify a bearer string.
/// </summary>
public sealed class CapabilityToken
{
    internal CapabilityToken(
        TenantId tenant,
        ActorId subject,
        IReadOnlyList<string> roles,
        IReadOnlyList<FormCapabilityAction> actions,
        DateTimeOffset expiresAt)
    {
        Tenant = tenant;
        Subject = subject;
        Roles = roles;
        Actions = actions;
        ExpiresAt = expiresAt;
    }

    /// <summary>The tenant this capability acts for (macaroon-bound; INV-S1 anchor).</summary>
    public TenantId Tenant { get; }

    /// <summary>The acting principal.</summary>
    public ActorId Subject { get; }

    /// <summary>The principal's roles, used for section-level read / write authorization.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>The coarse-grained actions this capability grants.</summary>
    public IReadOnlyList<FormCapabilityAction> Actions { get; }

    /// <summary>Absolute expiry; the engine re-checks this against the current time as defence-in-depth.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>True when this capability grants <paramref name="action"/>.</summary>
    public bool Allows(FormCapabilityAction action) => Actions.Contains(action);
}

/// <summary>The coarse-grained actions a <see cref="CapabilityToken"/> may grant.</summary>
public enum FormCapabilityAction
{
    /// <summary>Render and validate. Required by <see cref="IFormEngine.RenderAsync"/>.</summary>
    Read,

    /// <summary>Validate-for-save and persist. Required by <see cref="IFormEngine.ValidateAsync"/> and <see cref="IFormEngine.SaveAsync"/>.</summary>
    Write,
}
