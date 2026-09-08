namespace Harborline.Api.Foundation.Session;

/// <summary>
/// The claim type names used on session principals (ADR 0099 A6/C5). The cookie principal
/// carries ONLY <see cref="SessionId"/> (the opaque id); <c>OnValidatePrincipal</c> rehydrates
/// <see cref="Subject"/> / <see cref="TenantId"/> / roles from the SERVER-SIDE record, never
/// the cookie.
/// </summary>
public static class SessionClaimTypes
{
    /// <summary>
    /// The opaque session id claim on the minimal cookie principal. This is the ONLY claim
    /// <c>SignInAsync</c> serializes into the cookie (A6) — no <c>sub</c>/<c>tid</c>/roles ride
    /// the cookie.
    /// </summary>
    public const string SessionId = "sid";

    /// <summary>
    /// The user subject claim. Pinned to the literal <c>"sub"</c> (ADR 0099 C5), matching
    /// Bridge <c>IdentityEndpoints</c>' <c>ctx.User.FindFirst("sub")</c> read — NOT
    /// <c>ClaimTypes.NameIdentifier</c>. Populated only on the rehydrated principal, from the
    /// record's <see cref="SessionRecord.UserId"/>.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// The bound tenant claim on the rehydrated principal. Read from the SERVER-SIDE record's
    /// <see cref="SessionRecord.TenantId"/>, never the cookie (A6/S8).
    /// </summary>
    public const string TenantId = "tid";
}
