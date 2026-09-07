using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;

namespace Harborline.Api.Foundation.Packs.DependencyInjection;

/// <summary>
/// DI registration for the Pack Composer EXPORT + VERIFY half (B-1a). Registers the stateless
/// canonicalizer / validator / PII scanner / codec / exporter / verifier. It does NOT register a
/// signer (the authoring roster key is host-provided per call) nor an <see cref="Trust.IPackTrustStore"/>
/// (the host builds it from its own-roster + Harborline-channel roots) — those are wiring the host
/// owns. It also registers NO install engine and NO seed-layer services (that is B-1b).
/// </summary>
public static class PackServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pack export + verify services. Idempotent for the shared crypto verifier
    /// (<c>TryAdd</c>), so it composes with a host that already registered
    /// <see cref="IOperationVerifier"/>.
    /// </summary>
    public static IServiceCollection AddPackComposerExportVerify(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOperationVerifier, Ed25519Verifier>();

        services.TryAddSingleton<PackContentCanonicalizer>();
        services.TryAddSingleton<PackContentPiiScanner>();
        services.TryAddSingleton<PackValidator>();
        services.TryAddSingleton<PackFileCodec>();

        // ADR 0145 DCP export gate: the DCP canonicalizer (its own signed content-address leaf) + the
        // counsel-cleared register (config-driven, read from the committed embedded JSON) + the validator.
        services.TryAddSingleton<PackDcpCanonicalizer>();
        services.TryAddSingleton<IDcpCounselRegister>(_ => DcpCounselRegister.FromEmbeddedResource());
        services.TryAddSingleton<IDcpValidator, DcpValidator>();

        services.TryAddSingleton<IPackExporter, PackExporter>();
        services.TryAddSingleton<IPackVerifier, PackVerifier>();

        return services;
    }

    /// <summary>
    /// Registers the Pack Composer INSTALL engine (B-1b) on top of export/verify. Wires the
    /// <see cref="IPackInstaller"/> over the install-state store, the durable audit sink, and the ADR 0143
    /// admission port. The defaults are the in-memory store + in-memory audit + the FAIL-CLOSED
    /// <see cref="WorkflowRefusingPackContentAdmission"/> — a host MUST override the audit sink with a
    /// durable kernel-audit adapter and the admission port with the real workflow-admission adapter to
    /// install effecting (workflow) packs. The trust store + revocation list are supplied per install on
    /// the <see cref="PackInstallContext"/> (host-owned wiring), exactly as B-1a's trust roots are.
    /// </summary>
    public static IServiceCollection AddPackComposerInstall(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPackComposerExportVerify();

        services.TryAddSingleton<IPackInstallStore, InMemoryPackInstallStore>();
        services.TryAddSingleton<IPackInstallAudit, InMemoryPackInstallAudit>();
        services.TryAddSingleton<IRestrictingDefinitionKindValidator>(RestrictingDefinitionKindValidator.Shared);
        services.TryAddSingleton<IPackContentAdmission, WorkflowRefusingPackContentAdmission>();
        services.TryAddSingleton<IPackInstaller, PackInstaller>();

        return services;
    }

    /// <summary>
    /// Registers the app-layer FEATURE GRAPH read-model (G1 keystone; design note
    /// <c>_shared/design/app-layer-feature-graph-2026-07-07.md</c>): the rebuildable content-edge-index
    /// provider + the <see cref="IPackFeatureGraphReadModel"/> that assembles per-app contributions
    /// (grouped by pillar) and cross-app edges from install state. Read-only + derived — no new store, no
    /// manifest change (that is G2). Depends on the install store (registered by
    /// <see cref="AddPackComposerInstall"/> or a host-bound durable adapter), resolved lazily, so this may
    /// be called before/after the install registration.
    /// </summary>
    public static IServiceCollection AddPackFeatureGraph(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPackContentEdgeIndexProvider, InMemoryPackContentEdgeIndexProvider>();
        services.TryAddSingleton<IPackFeatureGraphReadModel, PackFeatureGraphReadModel>();

        return services;
    }
}
