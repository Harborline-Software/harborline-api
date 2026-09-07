namespace Harborline.Api.Foundation.Integrations;

/// <summary>
/// Marker interface tagging a concrete implementation as a mock Tier-2 vendor
/// provider. Co-located with the contract it implements
/// (<see cref="Captcha.InMemoryCaptchaVerifier"/>) per ADR 0096 Halt 1
/// (same-package mock-with-contracts layout).
/// </summary>
/// <remarks>
/// <para>
/// The marker is the positive-identification mechanism for the substrate-tier
/// production-safety property in ADR 0096 §D1c: at startup,
/// <see cref="DependencyInjection.MockProviderProductionGuardAssertion"/>
/// inspects the <see cref="Microsoft.Extensions.DependencyInjection.ServiceDescriptor"/>
/// collection and, for every descriptor whose
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceDescriptor.ImplementationType"/>
/// (or <see cref="Microsoft.Extensions.DependencyInjection.ServiceDescriptor.ImplementationInstance"/>
/// for instance registrations) implements this interface, requires either (a)
/// the corresponding real-adapter environment variable to be present (the
/// mapping is sourced from <see cref="DependencyInjection.IMockVendorEnvVarRegistry"/>),
/// or (b) the global opt-out env var <c>HARBORLINE_ALLOW_MOCK_PROVIDERS</c> to
/// parse to <c>true</c> via <see cref="bool.TryParse(string, out bool)"/>.
/// Otherwise startup fails with
/// <see cref="DependencyInjection.MockInProductionException"/>.
/// </para>
/// <para>
/// Marker membership is also enforced at compile time on the canonical mock
/// registration helper
/// <see cref="DependencyInjection.VendorProviderServiceCollectionExtensions.AddHarborlineVendorProvider{TContract, TConcrete}(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.DependencyInjection.ServiceLifetime)"/>
/// via the generic constraint
/// <c>where TConcrete : class, TContract, IMockVendorProvider</c>. A mock
/// concrete that forgets to implement the marker is a compile-error, not a
/// silent runtime no-op. Real vendor adapters register through the separate
/// <see cref="DependencyInjection.VendorProviderServiceCollectionExtensions.UseVendorProviderIfConfigured{TContract, TReal}(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
/// helper which carries NO marker constraint — real adapters MUST NOT
/// implement <see cref="IMockVendorProvider"/>.
/// </para>
/// <para>
/// <strong>Tier-2 contract naming conventions documented here per ADR 0096
/// §D5 (the marker xmldoc is the canonical home for the convention text):</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Egress-surface contracts are named <c>IXProvider</c></strong>
///     (e.g., future <c>IBlobStorageProvider</c> and
///     <c>IExternalIdpProvider</c>).
///     This is the default shape for Tier-2 substrate contracts.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Configuration / option-binding types follow
///     <c>IXProviderConfig</c></strong> (e.g., the existing
///     <see cref="Captcha.ICaptchaProviderConfig"/> in this assembly — the
///     convention is half-applied in the substrate today, predating ADR 0096).
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong><see cref="Captcha.ICaptchaVerifier"/> is the grandfathered
///     egress-surface exception</strong> per ADR 0059 / W#28 Phase 3, predating
///     the <c>IXProvider</c> naming convention. The action-noun naming is
///     preserved for backward compatibility; the discriminating principle —
///     <em>Verifier = stateless one-call ack-only</em>;
///     <em>Provider = stateful or multi-call</em> — is documented in
///     ADR 0096 §D5 and may apply to future Tier-2 contracts that follow the
///     Verifier shape with explicit xmldoc rationale.
///     </description>
///   </item>
/// </list>
/// <para>
/// The Tier-2 substrate pattern identifies its members via
/// <see cref="IMockVendorProvider"/> marker membership and
/// <see cref="Harborline.Api.Foundation.Catalog.Bundles.ProviderCategory"/> enum
/// participation — NOT via <c>IXProvider</c> naming convention. The marker +
/// descriptor IS the contract; interface naming is conventional packaging.
/// </para>
/// </remarks>
public interface IMockVendorProvider
{
}
