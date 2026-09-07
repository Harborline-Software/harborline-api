using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// Mints a bearer macaroon encoding a form capability. The complement of
/// <see cref="IFormCapabilityVerifier"/>: what this issuer writes, that
/// verifier reads. Issuance is a privileged operation (the caller already
/// established the tenant / subject / roles / actions out of band, e.g. from
/// a session or an admin grant); the issuer does not itself authorize.
/// </summary>
public interface IFormCapabilityIssuer
{
    /// <summary>
    /// Mints a macaroon granting <paramref name="subject"/> the given
    /// <paramref name="roles"/> and <paramref name="actions"/> for
    /// <paramref name="tenant"/>, expiring at <paramref name="expiresAt"/>.
    /// Returns the base64url-encoded token.
    /// </summary>
    Task<string> IssueAsync(
        TenantId tenant,
        ActorId subject,
        IReadOnlyList<string> roles,
        IReadOnlyList<FormCapabilityAction> actions,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);
}
