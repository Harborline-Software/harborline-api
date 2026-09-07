using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model.Scoring;
using Harborline.Api.Blocks.Assets.Registry.Projection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.Blocks.Assets.Registry.DependencyInjection;

/// <summary>
/// DI extensions for the Asset Type System substrate (ADR 0101 Rev 3.1 Wave 1).
/// </summary>
public static class AssetRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory Asset-Type-System surface:
    /// <list type="bullet">
    ///   <item><see cref="IRegistryAuditLog"/> → <see cref="InMemoryRegistryAuditLog"/> (the durable-layer seam)</item>
    ///   <item><see cref="IEntityTypeRegistry"/> → <see cref="InMemoryEntityTypeRegistry"/></item>
    ///   <item><see cref="IRegistryEntityRepository"/> → <see cref="InMemoryRegistryEntityRepository"/></item>
    ///   <item><see cref="ITypedRelationshipStore"/> → <see cref="InMemoryTypedRelationshipStore"/></item>
    ///   <item><see cref="IConditionAssessmentStore"/> → <see cref="InMemoryConditionAssessmentStore"/></item>
    ///   <item><see cref="IScoringCascadeResolver"/> → <see cref="ScoringCascadeResolver"/></item>
    /// </list>
    /// Suitable for tests, prototyping, and kitchen-sink demos. Replace the audit log and stores
    /// with persistence-backed implementations behind the same interfaces in production hosts.
    /// </summary>
    public static IServiceCollection AddInMemoryAssetTypeSystem(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRegistryAuditLog, InMemoryRegistryAuditLog>();
        services.AddSingleton<IEntityTypeRegistry, InMemoryEntityTypeRegistry>();
        services.AddSingleton<IRegistryEntityRepository, InMemoryRegistryEntityRepository>();
        services.AddSingleton<ITypedRelationshipStore, InMemoryTypedRelationshipStore>();
        services.AddSingleton<IConditionAssessmentStore, InMemoryConditionAssessmentStore>();
        services.AddSingleton<IFormSubmissionRecordStore, InMemoryFormSubmissionRecordStore>();
        services.AddSingleton<IScoringCascadeResolver, ScoringCascadeResolver>();
        services.AddSingleton<IStandardCatalogSeedStore, InMemoryStandardCatalogSeedStore>();

        return services;
    }

    /// <summary>
    /// Swaps the registry's X-AUDIT journal onto the <b>durable</b> foundation audit substrate
    /// (<see cref="Harborline.Api.Foundation.Assets.Audit.IAuditLog"/>) — ADR 0101 Rev 3.1 Wave 2 gate
    /// <b>U1</b>. Replaces whatever <see cref="IRegistryAuditLog"/> was previously registered (e.g.
    /// the in-memory Wave-1 default from <see cref="AddInMemoryAssetTypeSystem"/>) with
    /// <see cref="FoundationBackedRegistryAuditLog"/>, so every registry mutation rides the same
    /// hash-chained append-only log as the forms engine and entity/hierarchy flows.
    /// </summary>
    /// <remarks>
    /// The host MUST have registered <see cref="Harborline.Api.Foundation.Assets.Audit.IAuditLog"/>
    /// (the foundation audit substrate) before resolving the registry stores; the adapter's
    /// constructor fails closed if it is absent. Call this AFTER
    /// <see cref="AddInMemoryAssetTypeSystem"/> when composing a live node host — the in-memory audit
    /// stays available for tests and demos that do not opt in.
    /// </remarks>
    public static IServiceCollection AddDurableAssetRegistryAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IRegistryAuditLog, FoundationBackedRegistryAuditLog>());

        return services;
    }

    /// <summary>
    /// Registers the Wave-5 durable spatial-frame-descriptor store (ADR 0101 Rev 3.2 [A10]/[A13];
    /// ADR 0168 OQ-1 ruling): <c>Replace</c>s <see cref="Services.Spatial.ISpatialFrameDescriptorStore"/>
    /// with the <b>package-side adapter</b>
    /// (<see cref="Services.Spatial.FoundationBackedSpatialFrameDescriptorStore"/>) — the audit-precedent
    /// registration model — and registers the fail-closed default
    /// <see cref="Services.Spatial.IFrameEpochAuthority"/> (which REFUSES every mint).
    /// </summary>
    /// <remarks>
    /// <b>Call ordering ([A10]):</b> call this following <see cref="AddDurableAssetRegistryAudit"/>.
    /// The host must supply, by ordinary registration, the foundation port
    /// (<see cref="Services.Spatial.ISpatialFrameDescriptorPort"/> — the durable host
    /// implementation) and must <c>services.Replace</c> <em>the authority</em> with its home-claim
    /// implementation. A host must NEVER re-register <c>ISpatialFrameDescriptorStore</c> itself:
    /// replacing the store interface evicts the package adapter and, with it, the only code that can
    /// call the <c>internal</c> tenant guard. The host composition hard-fail asserts all three
    /// resolutions.
    /// </remarks>
    public static IServiceCollection AddDurableAssetRegistryDescriptorStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<Services.Spatial.IFrameEpochAuthority,
            Services.Spatial.RefusingFrameEpochAuthority>();
        services.Replace(ServiceDescriptor.Singleton<Services.Spatial.ISpatialFrameDescriptorStore,
            Services.Spatial.FoundationBackedSpatialFrameDescriptorStore>());

        return services;
    }

    /// <summary>
    /// Registers the Wave-2 condition-capture projection (ADR 0101 Rev 3.1 / A3, annex D-M): the
    /// tenant-scoped <see cref="IConditionRatingFieldBindingStore"/> (in-memory) and the
    /// <see cref="ConditionAssessmentProjector"/> as an <see cref="IFormSubmitProjection"/> hook, so a
    /// form submission carrying a condition-rating field writes the entity's typed
    /// <see cref="Harborline.Api.Blocks.Assets.Registry.Model.ConditionAssessment"/>.
    /// </summary>
    /// <remarks>
    /// Requires the Asset Type System stores (<see cref="AddInMemoryAssetTypeSystem"/>) to be
    /// registered for the projector's dependencies. The projector only fires through the forms
    /// post-submit seam — a host wires that with <c>AddFormSubmitProjectionDecoration()</c> (or by
    /// invoking the runner on its submit route). Registered via <c>TryAddEnumerable</c> so it composes
    /// with other projections and is idempotent.
    /// </remarks>
    public static IServiceCollection AddConditionCaptureProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConditionRatingFieldBindingStore, InMemoryConditionRatingFieldBindingStore>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFormSubmitProjection, ConditionAssessmentProjector>());

        return services;
    }

    /// <summary>
    /// Registers the generic runtime form-fill projection (#144): the
    /// <see cref="FormSubmissionRecordProjection"/> as an <see cref="IFormSubmitProjection"/> hook, so a
    /// form submission that carries a resolvable case ref writes a
    /// <see cref="Harborline.Api.Blocks.Assets.Registry.Model.FormSubmissionRecord"/> linking the submission to
    /// that record. Unlike the condition-capture projection this is NOT binding-gated — any submission
    /// filled into a record leaves the link.
    /// </summary>
    /// <remarks>
    /// Requires the Asset Type System stores (<see cref="AddInMemoryAssetTypeSystem"/>) — the
    /// <see cref="IFormSubmissionRecordStore"/> + <see cref="IRegistryEntityRepository"/> the projection
    /// depends on. Fires only through the forms post-submit seam (wired by
    /// <c>AddFormSubmitProjectionDecoration()</c>). Registered via <c>TryAddEnumerable</c> so it composes
    /// with the condition projector and is idempotent.
    /// </remarks>
    public static IServiceCollection AddFormSubmissionRecordProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFormSubmitProjection, FormSubmissionRecordProjection>());

        return services;
    }
}
