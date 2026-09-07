using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Recovery.Shred;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// DI registration + the fail-closed composition-time boot-gate for the ADR 0142
/// legal-hold registry and the ADR 0137 §D8 retention→shred propose-then-release
/// scheduler.
/// </summary>
public static class LegalHoldServiceCollectionExtensions
{
    /// <summary>
    /// Registers the legal-hold subsystem: the append-only <see cref="ILegalHoldStore"/>
    /// (in-memory default — override for production), the fail-closed
    /// <see cref="ILegalHoldRegistry"/> gate, the place/release <see cref="ILegalHoldService"/>,
    /// and the retention→shred <see cref="IRetentionShredScheduler"/> (propose) +
    /// <see cref="IShredReleaseService"/> (release). The service requires an audited path, so it
    /// registers only when an <c>IAuditTrail</c> + <c>IOperationSigner</c> are present.
    /// </summary>
    /// <remarks>
    /// Call after <c>AddHarborlineRecoveryCoordinator</c> so the same <see cref="ILegalHoldRegistry"/>
    /// this method's default resolves to is the one injected into the erasure service (both use
    /// <c>TryAddSingleton</c> for the store + registry, so they agree on one instance). A host that
    /// runs any shred path MUST additionally register a durable store and call
    /// <see cref="RequireDurableLegalHoldStore"/> at its composition root.
    /// </remarks>
    public static IServiceCollection AddHarborlineLegalHold(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILegalHoldStore, InMemoryLegalHoldStore>();
        services.TryAddSingleton<ILegalHoldRegistry, LegalHoldRegistry>();
        services.TryAddSingleton<ILegalHoldService, LegalHoldService>();

        // Retention→shred: the propose half needs only the fail-closed hold gate; the release
        // half needs only the gated erasure SERVICE (never the registry — it cannot bypass the gate).
        services.TryAddSingleton<IRetentionShredScheduler, RetentionShredScheduler>();
        services.TryAddSingleton<IShredReleaseService, ShredReleaseService>();

        return services;
    }

    /// <summary>
    /// Fail-closed durable-store gate for the legal-hold registry (ADR 0142 §D4), mirroring
    /// <c>RequireDurableErasureStores</c> with inverted polarity. Throws
    /// <see cref="InvalidOperationException"/> unless <see cref="ILegalHoldStore"/> is registered
    /// with a NON-volatile implementation — i.e. the restart-volatile
    /// <see cref="InMemoryLegalHoldStore"/> default has been overridden. A host that runs any shred
    /// path (scheduled retention→shred OR manual GDPR crypto-shred) MUST call this at its
    /// composition root after registering its durable, append-only store.
    /// </summary>
    /// <remarks>
    /// <b>Why this is a structural gate, not a comment.</b> A hold is a compliance record: it is
    /// what keeps a subject under litigation hold un-shreddable. If the store resolves to the
    /// in-memory default, a process restart silently <em>forgets the hold</em> — the fail-closed
    /// gate then answers "nothing held" and a held subject becomes shreddable, an irreversible
    /// spoliation event. This turns that silent-default hole into a deterministic startup failure.
    /// The gate is conservative: it rejects ONLY the known in-memory default TYPE; any other
    /// implementation (a different concrete type, an instance, or a factory) is accepted as the
    /// host's deliberate durable choice (the gate closes the silent-default hole, which is the risk;
    /// it cannot prove durability of an arbitrary type). The check runs against the
    /// <see cref="IServiceCollection"/> at composition time, so it cannot be defeated by lazy
    /// resolution and does not build the provider.
    /// </remarks>
    /// <param name="services">The composition-root service collection to validate.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The hold store is unregistered or still resolves to its restart-volatile in-memory default.
    /// </exception>
    public static IServiceCollection RequireDurableLegalHoldStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Walk from the end: the effective (resolved) registration for a service type is the LAST
        // descriptor added for it.
        ServiceDescriptor? effective = null;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ILegalHoldStore))
            {
                effective = services[i];
                break;
            }
        }

        if (effective is null)
        {
            throw new InvalidOperationException(
                "Legal-hold durable-store gate: no ILegalHoldStore is registered. A host wiring any "
                + "shred path MUST register a durable, append-only ILegalHoldStore (a lost hold silently "
                + "un-holds a subject on restart — a spoliation risk) before calling "
                + "RequireDurableLegalHoldStore().");
        }

        // Only the implementation TYPE distinguishes the volatile default. An instance or factory
        // registration is, by construction, NOT the default TryAddSingleton<TInterface, TImpl>() and
        // is treated as the host's deliberate durable wiring.
        if (effective.ImplementationType == typeof(InMemoryLegalHoldStore))
        {
            throw new InvalidOperationException(
                "Legal-hold durable-store gate: ILegalHoldStore still resolves to the restart-volatile "
                + "in-memory default (InMemoryLegalHoldStore). A lost hold silently un-holds a subject on "
                + "restart, so the fail-closed pre-shred gate would then answer 'nothing held' and a held "
                + "subject would become shreddable (an irreversible spoliation event). Register a durable, "
                + "append-only implementation (e.g. FileSystemLegalHoldStore, or an encrypted-DB-backed "
                + "store) before calling RequireDurableLegalHoldStore().");
        }

        return services;
    }
}
