using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Macaroons;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Kernel.Schema;

namespace Harborline.Api.Foundation.Forms.Engine.DependencyInjection;

/// <summary>
/// DI registration extensions for the dynamic-forms engine (ADR 0055 §3.1) and
/// its macaroon capability primitives.
/// </summary>
public static class FormEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="FormEngine"/> as the singleton
    /// <see cref="IFormEngine"/>, along with the supplied (or default)
    /// <see cref="FormEngineOptions"/> and a <see cref="TimeProvider"/> fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine composes substrate the <b>host registers separately</b>:
    /// <see cref="Harborline.Api.Foundation.Forms.IFormDefinitionStore"/> (e.g.
    /// <c>AddEntityStoreFormDefinitionStore</c>),
    /// <see cref="Harborline.Api.Kernel.Schema.ISchemaRegistry"/>
    /// (<c>AddHarborlineKernelSchemaRegistry</c>),
    /// <see cref="Harborline.Api.Foundation.Assets.Entities.IEntityStore"/>
    /// (<c>AddHarborlineAssetsInMemory</c>, or a durable adapter registration),
    /// <see cref="Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor"/>, and
    /// <see cref="Harborline.Api.Foundation.Assets.Audit.IAuditLog"/>. Register those
    /// before resolving <see cref="IFormEngine"/>.
    /// </para>
    /// <para>
    /// All three registrations are idempotent (<c>TryAdd</c>): a host that has
    /// already registered options, a <see cref="TimeProvider"/>, or an engine
    /// keeps its own. <see cref="TimeProvider.System"/> is the fallback clock.
    /// </para>
    /// <para>
    /// <b>SPINE-2 governance seam (F-11).</b> The engine is registered via a factory
    /// that resolves the OPTIONAL <see cref="IFieldPolicyEnforcer"/> +
    /// <see cref="IAspectResolver"/> with <c>GetService</c> (nullable). When a host has
    /// called <c>AddHarborlineGovernance()</c> (plus its residency/retention substrate)
    /// before this, the engine enforces classification on field VALUES; otherwise it uses
    /// the legacy PII-only path. A host that also sets
    /// <see cref="FormEngineOptions.RequireGovernanceEnforcement"/> makes a missing seam a
    /// hard boot failure (the composition-root fail-closed gate).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineFormEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IEntityMutationStore> mutationStore,
        FormEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mutationStore);
        services.TryAddSingleton(options ?? new FormEngineOptions());
        services.TryAddSingleton<IAuthorizedFormEntityWriter>(sp => new AuthorizedFormEntityWriter(
            mutationStore(sp)));
        services.TryAddSingleton<IFormEngine>(sp => new FormEngine(
            sp.GetRequiredService<Harborline.Api.Foundation.Forms.IFormDefinitionStore>(),
            sp.GetRequiredService<ISchemaRegistry>(),
            sp.GetRequiredService<IEntityStore>(),
            sp.GetRequiredService<IFieldEncryptor>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<FormEngineOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuthorizationGate>(),
            sp.GetRequiredService<IAuthorizedFormEntityWriter>(),
            sp.GetRequiredService<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>(),
            sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationSigner>(),
            sp.GetService<IFieldPolicyEnforcer>(),
            sp.GetService<IAspectResolver>(),
            // D4 reuse cascade (optional): when a host has registered AddReuseResolver() (+ an
            // IReusableUnitStore), the render path expands Reference nodes CP-locked before building the
            // view; absent, a Reference node simply does not expand (identity render — back-compat).
            sp.GetService<Harborline.Api.Foundation.Forms.IReuseResolver>(),
            // Decrypt-on-render seam (ADR 0055 Rev 10 OQ-A): OPTIONAL and fail-closed. When a host
            // has registered the LIVE recovery decrypt substrate (IFieldDecryptor +
            // IDecryptCapabilityProvider, e.g. via AddHarborlineRecoveryCoordinator), a token carrying
            // the forms:decrypt-sensitive PBAC permission renders Sensitive at-rest values with an
            // audited just-in-time decrypt; absent EITHER registration, every Sensitive field stays
            // withheld exactly as before (no mock resolver, no silent fallback).
            sp.GetService<IFieldDecryptor>(),
            sp.GetService<Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider>(),
            // Ticket 150: optional logger so render-time rule-projection degrades are observable
            // (a warning naming the responsible definition); NullLogger fallback when unregistered.
            sp.GetService<Microsoft.Extensions.Logging.ILogger<FormEngine>>()));
        return services;
    }

    /// <summary>
    /// Wraps the registered <see cref="IFormEngine"/> in a <see cref="ProjectingFormEngine"/> so that
    /// a successful <see cref="IFormEngine.SaveAsync"/> fires the registered post-submit projections
    /// (ADR 0101 Rev 3.1 Wave 2 — the forms field-kind extension point). Must be called AFTER
    /// <see cref="AddHarborlineFormEngine"/>. Idempotent-safe: calling it twice does not double-wrap
    /// (a second call detects the decorator already in place and returns).
    /// </summary>
    /// <remarks>
    /// Registers the default <see cref="IFormSubmitProjectionRunner"/> (via
    /// <c>AddFormSubmitProjections</c>) if the host has not already; individual
    /// <see cref="IFormSubmitProjection"/> hooks are registered by their owning packages. The
    /// decorator is transparent when no projection matches a form.
    /// </remarks>
    public static IServiceCollection AddFormSubmitProjectionDecoration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFormSubmitProjections();

        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IFormEngine))
            ?? throw new InvalidOperationException(
                "AddHarborlineFormEngine must be called before AddFormSubmitProjectionDecoration.");

        // Already decorated — don't double-wrap (idempotent for a host that opts in more than once).
        if (descriptor.ImplementationType == typeof(ProjectingFormEngine))
        {
            return services;
        }

        services.Remove(descriptor);
        services.AddSingleton<IFormEngine>(sp => new ProjectingFormEngine(
            MaterializeInner(descriptor, sp),
            sp.GetRequiredService<IFormSubmitProjectionRunner>()));
        return services;
    }

    private static IFormEngine MaterializeInner(ServiceDescriptor descriptor, IServiceProvider sp) => descriptor switch
    {
        { ImplementationInstance: IFormEngine instance } => instance,
        { ImplementationFactory: { } factory } => (IFormEngine)factory(sp),
        { ImplementationType: { } type } => (IFormEngine)ActivatorUtilities.CreateInstance(sp, type),
        _ => throw new InvalidOperationException("The registered IFormEngine descriptor cannot be materialized."),
    };

    /// <summary>
    /// Registers the reference macaroon form-capability pair —
    /// <see cref="MacaroonFormCapabilityVerifier"/> as
    /// <see cref="IFormCapabilityVerifier"/> and
    /// <see cref="MacaroonFormCapabilityIssuer"/> as
    /// <see cref="IFormCapabilityIssuer"/>.
    /// </summary>
    /// <remarks>
    /// Composes the foundation macaroon primitives the host registers
    /// separately: the verifier resolves
    /// <see cref="Harborline.Api.Foundation.Macaroons.IRootKeyStore"/> and the issuer
    /// resolves <see cref="Harborline.Api.Foundation.Macaroons.IMacaroonIssuer"/>
    /// (cf. <c>HarborlineDecentralizationExtensions</c>). Both registrations are
    /// idempotent (<c>TryAdd</c>).
    /// </remarks>
    public static IServiceCollection AddMacaroonFormCapabilities(
        this IServiceCollection services,
        string location = MacaroonFormCapabilityIssuer.DefaultLocation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(location);
        services.TryAddSingleton<IFormCapabilityVerifier, MacaroonFormCapabilityVerifier>();
        services.TryAddSingleton<IFormCapabilityIssuer>(sp =>
            new MacaroonFormCapabilityIssuer(sp.GetRequiredService<IMacaroonIssuer>(), location));
        return services;
    }
}
