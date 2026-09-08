using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Crdt.GarbageCollection;
using Harborline.Api.Kernel.Crdt.SnapshotScheduling;

namespace Harborline.Api.Kernel.Crdt.DependencyInjection;

/// <summary>
/// DI extensions for registering the Harborline CRDT engine (paper §2.2, §9; ADR 0008).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ICrdtEngine"/> as a singleton, backed by the YDotNet/yrs
    /// engine of record. Uses <c>TryAddSingleton</c> so a prior explicit
    /// registration wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The production adapter is <see cref="YDotNetCrdtEngine"/> (Yjs/yrs via
    /// YDotNet 0.6.0). See <c>packages/kernel-crdt/SPIKE-OUTCOME.md</c> for the
    /// validation history and ADR 0008 for the controlling selection.
    /// </para>
    /// <para>
    /// Deterministic unit tests that do not need real CRDT merge semantics may call
    /// <see cref="AddHarborlineCrdtEngineStub"/> before any other registration, or
    /// call <c>services.AddSingleton&lt;ICrdtEngine, StubCrdtEngine&gt;()</c> directly.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineCrdtEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        services.TryAddSingleton<ICrdtProjectionRegistry, CrdtProjectionRegistry>();
        return services;
    }

    /// <summary>
    /// Register the YDotNet (Yjs/yrs) CRDT backend explicitly. Use this when you
    /// want to be explicit in host wiring rather than relying on the default.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineCrdtEngineYDotNet(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        services.TryAddSingleton<ICrdtProjectionRegistry, CrdtProjectionRegistry>();
        return services;
    }

    /// <summary>
    /// Register the test-only in-memory fake. See the banner in
    /// <c>Backends/StubCrdtEngine.cs</c> — the stub is not a production fallback.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineCrdtEngineStub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICrdtEngine, StubCrdtEngine>();
        services.TryAddSingleton<ICrdtProjectionRegistry, CrdtProjectionRegistry>();
        return services;
    }

    /// <summary>
    /// Register the paper §9 CRDT growth-mitigation services: shallow-snapshot manager,
    /// default (conservative) policy, and document garbage collector facade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers as singletons:
    /// <list type="bullet">
    ///   <item><see cref="IShallowSnapshotPolicy"/> → <see cref="NeverShallowSnapshotPolicy"/>
    ///     (paper §9: "default policy is conservative: full history is retained").</item>
    ///   <item><see cref="IShallowSnapshotManager"/> → <see cref="ShallowSnapshotManager"/>.</item>
    ///   <item><see cref="IDocumentGarbageCollector"/> → <see cref="DocumentGarbageCollector"/>.</item>
    /// </list>
    /// Uses <c>TryAddSingleton</c> throughout so a host can override any of these with a
    /// per-document-type policy or a differently-wired manager before calling this
    /// extension.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineCrdtGarbageCollection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IShallowSnapshotPolicy, NeverShallowSnapshotPolicy>();
        services.TryAddSingleton<IShallowSnapshotManager, ShallowSnapshotManager>();
        services.TryAddSingleton<IDocumentGarbageCollector, DocumentGarbageCollector>();
        return services;
    }
}
