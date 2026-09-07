using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// Verifies a bearer macaroon and, on success, mints an already-verified
/// <see cref="CapabilityToken"/>. The verifier is the ONLY component that
/// produces a <see cref="CapabilityToken"/> (the token's constructor is
/// <see langword="internal"/>); possessing a token is therefore proof the
/// macaroon's signature chain, tenant binding, action grant, and expiry have
/// all been checked. <see cref="IFormEngine"/> trusts the token and never
/// re-verifies a bearer string.
/// </summary>
public interface IFormCapabilityVerifier
{
    /// <summary>
    /// Verifies <paramref name="tokenBase64Url"/> and mints a
    /// <see cref="CapabilityToken"/>. Throws <see cref="CapabilityDeniedException"/>
    /// on any failure (bad encoding, unknown root key, signature mismatch,
    /// malformed / unknown / missing caveat, or expiry).
    /// </summary>
    /// <param name="tokenBase64Url">The base64url-encoded macaroon.</param>
    /// <param name="now">The current time, checked against the macaroon's expiry caveat.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CapabilityToken> VerifyAsync(string tokenBase64Url, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// Raised when a form capability cannot be verified. Carries an opaque
/// <see cref="CapabilityIdOrToken"/> (the macaroon identifier, or a truncated
/// token prefix when decoding failed before an identifier was available) and a
/// machine-stable <see cref="Reason"/>. The message and reason intentionally
/// never echo caveat secrets or the full token.
/// </summary>
public sealed class CapabilityDeniedException : Exception
{
    /// <summary>Creates the exception with the offending identifier and a reason.</summary>
    public CapabilityDeniedException(string capabilityIdOrToken, string reason)
        : base($"Form capability denied for {capabilityIdOrToken}: {reason}")
    {
        CapabilityIdOrToken = capabilityIdOrToken;
        Reason = reason;
    }

    /// <summary>The macaroon identifier, or a truncated token prefix.</summary>
    public string CapabilityIdOrToken { get; }

    /// <summary>A machine-stable reason code (e.g. <c>signature-mismatch</c>).</summary>
    public string Reason { get; }
}
