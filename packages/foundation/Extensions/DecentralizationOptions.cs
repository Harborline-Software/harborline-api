using Harborline.Api.Foundation.PolicyEvaluator;

namespace Harborline.Api.Foundation.Extensions;

/// <summary>
/// Options for <see cref="HarborlineDecentralizationExtensions.AddHarborlineDecentralization"/>.
/// </summary>
/// <remarks>
/// These options gate the dev-only in-memory key material (see <see cref="EnableDevKeyMaterial"/>)
/// and let callers customize the <see cref="Harborline.Api.Foundation.PolicyEvaluator.PolicyModel"/> that
/// the registered <see cref="IPermissionEvaluator"/> consults.
/// </remarks>
public sealed class DecentralizationOptions
{
    /// <summary>
    /// <b>DO NOT enable in production.</b> When <c>true</c>, registers an in-memory
    /// <see cref="Harborline.Api.Foundation.Crypto.DevKeyStore"/> and
    /// <see cref="Harborline.Api.Foundation.Macaroons.InMemoryRootKeyStore"/>, plus the default
    /// <see cref="Harborline.Api.Foundation.Macaroons.IMacaroonIssuer"/> /
    /// <see cref="Harborline.Api.Foundation.Macaroons.IMacaroonVerifier"/> pair that depends on them.
    /// A <see cref="Microsoft.Extensions.Hosting.IHostedService"/> emits a
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> on startup so the non-production
    /// wiring is visible in logs. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Production callers should leave this <c>false</c> and register their own
    /// <see cref="Harborline.Api.Foundation.Macaroons.IRootKeyStore"/>,
    /// <see cref="Harborline.Api.Foundation.Macaroons.IMacaroonIssuer"/>, and
    /// <see cref="Harborline.Api.Foundation.Macaroons.IMacaroonVerifier"/> implementations — backed by
    /// KMS, HSM, or the OS keyring — before using macaroons.
    /// </remarks>
    public bool EnableDevKeyMaterial { get; set; } = false;

    /// <summary>
    /// Fluent callback for configuring the default <see cref="Harborline.Api.Foundation.PolicyEvaluator.PolicyModel"/>
    /// that the registered <see cref="IPermissionEvaluator"/> consults. The builder is materialized
    /// at registration time and the resulting <see cref="Harborline.Api.Foundation.PolicyEvaluator.PolicyModel"/>
    /// is registered as a singleton. If <c>null</c>, an empty policy model is registered.
    /// </summary>
    public Action<PolicyModelBuilder>? PolicyModel { get; set; }
}
