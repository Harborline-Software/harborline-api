using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.DependencyInjection;

/// <summary>
/// DI registration for the foundation-tier recovery orchestration surface
/// (ADR 0046 sub-patterns #48a / #48e / #48f). Kernel-tier crypto primitives
/// (Ed25519, X25519, SqlCipher key derivation) live in
/// <c>Harborline.Api.Kernel.Security</c>; register those via
/// <c>AddHarborlineKernelSecurity</c> first.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Phase 1 G6 recovery SUBSTRATE per ADR 0046:
    /// <see cref="IRecoveryClock"/> (defaulting to
    /// <see cref="SystemRecoveryClock"/>), and
    /// <see cref="IRecoveryStateStore"/> (defaulting to
    /// <see cref="InMemoryRecoveryStateStore"/> until a SQLCipher-backed
    /// store ships in Phase 1.x).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately does NOT register
    /// <see cref="IRecoveryCoordinator"/>. The coordinator requires an
    /// <see cref="IDisputerValidator"/> — the owner's authorized
    /// disputer keys — which this method cannot know. Registering it
    /// here produced a descriptor the container could never build, and
    /// every host that called this method for the crypto substrate
    /// acquired it silently (ticket 091). Under <c>ValidateOnBuild</c>
    /// the host died at startup; outside Development the same descriptor
    /// was latent and would have thrown at the first attempted recovery
    /// instead — the worst possible moment, since recovery is where a
    /// user arrives having already lost something. A registration that
    /// cannot resolve is worse than an absent one.
    /// </para>
    /// <para>
    /// A host that wants the multi-sig recovery surface registers the two
    /// together: an <see cref="IDisputerValidator"/> — typically
    /// <see cref="FixedDisputerValidator"/> constructed with the owner's
    /// NodeIdentity public key(s) — and then
    /// <see cref="RecoveryCoordinator"/>. Registering the coordinator
    /// without a validator reproduces ticket 091.
    /// </para>
    /// <para>
    /// Production hosts should also override
    /// <see cref="IRecoveryStateStore"/> with a durable implementation —
    /// the in-memory default does not survive process restart, which
    /// breaks the 7-day grace-window survivability requirement from the
    /// Phase 1 plan.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineRecoveryCoordinator(
        this IServiceCollection services,
        Action<RecoveryCoordinatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RecoveryCoordinatorOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IRecoveryClock, SystemRecoveryClock>();
        services.TryAddSingleton<IRecoveryStateStore, InMemoryRecoveryStateStore>();

        // ADR 0067 §5.3.1 — IDecryptCapabilityProvider seam (W#48 Phase
        // 1b companion). The interface lives in foundation/Crypto/ for
        // cycle-safety; the ui-core AddHarborlineIntegrationAtlas() guard
        // throws InvalidOperationException when this is missing.
        services.TryAddSingleton<IDecryptCapabilityProvider, TenantKeyDecryptCapabilityProvider>();

        // ADR 0046-A4/A5 — field-encryption substrate (W#32). The decryptor
        // requires BOTH IAuditTrail + IOperationSigner together (audit-enabled
        // overload) OR neither (audit-disabled overload). The factory throws
        // on first resolution if exactly one is registered (A5.7).
        services.TryAddSingleton<IFieldEncryptor, TenantKeyProviderFieldEncryptor>();
        services.TryAddSingleton<IFieldDecryptor>(sp =>
        {
            var tenantKeys = sp.GetRequiredService<ITenantKeyProvider>();
            var clock = sp.GetRequiredService<IRecoveryClock>();
            var auditTrail = sp.GetService<IAuditTrail>();
            var signer = sp.GetService<IOperationSigner>();
            return (auditTrail, signer) switch
            {
                (null, null) => new TenantKeyProviderFieldDecryptor(tenantKeys, clock),
                (not null, not null) => new TenantKeyProviderFieldDecryptor(tenantKeys, auditTrail, signer, clock),
                _ => throw new InvalidOperationException(
                    "Field-encryption decryptor requires both IAuditTrail and IOperationSigner registered, or neither. "
                    + $"Mid-state misconfiguration: IAuditTrail={(auditTrail is null ? "null" : "registered")}, "
                    + $"IOperationSigner={(signer is null ? "null" : "registered")}.")
            };
        });

        // ADR 0135 GDPR direction — crypto-shredding subject erasure.
        // In-memory defaults are restart-volatile; production hosts MUST override
        // ISubjectErasureRegistry + ISubjectTombstoneStore with durable, append-only
        // implementations (the tombstone is a compliance record per ADR 0068 §1.3).
        services.TryAddSingleton<ISubjectErasureRegistry, InMemorySubjectErasureRegistry>();
        services.TryAddSingleton<ISubjectTombstoneStore, InMemorySubjectTombstoneStore>();

        // ADR 0142 — the fail-closed pre-shred LEGAL-HOLD gate. In-memory defaults are
        // restart-volatile; a host that runs any shred path MUST override ILegalHoldStore
        // with a durable, append-only implementation and call RequireDurableLegalHoldStore().
        // The registry is injected into the erasure service below so the manual GDPR crypto-
        // shred path is hold-gated by construction (the scheduled path is gated through the
        // same service). A default (empty) registry answers "nothing held" — no behaviour
        // change for a host that places no holds — but the gate is now live.
        services.TryAddSingleton<ILegalHoldStore, InMemoryLegalHoldStore>();
        services.TryAddSingleton<ILegalHoldRegistry, LegalHoldRegistry>();

        // Per-subject field encrypt/decrypt — the same envelope shape as the
        // tenant-DEK pair, but sealed under a per-subject sub-key so a single
        // subject can be crypto-shredded without affecting others.
        services.TryAddSingleton<ISubjectFieldEncryptor>(sp =>
            new SubjectKeyFieldEncryptor(
                sp.GetRequiredService<ITenantKeyProvider>(),
                sp.GetRequiredService<ISubjectErasureRegistry>()));
        services.TryAddSingleton<ISubjectFieldDecryptor>(sp =>
        {
            var tenantKeys = sp.GetRequiredService<ITenantKeyProvider>();
            var erasure = sp.GetRequiredService<ISubjectErasureRegistry>();
            var clock = sp.GetRequiredService<IRecoveryClock>();
            var auditTrail = sp.GetService<IAuditTrail>();
            var signer = sp.GetService<IOperationSigner>();
            return (auditTrail, signer) switch
            {
                (null, null) => new SubjectKeyFieldDecryptor(tenantKeys, erasure, clock),
                (not null, not null) => new SubjectKeyFieldDecryptor(tenantKeys, erasure, auditTrail, signer, clock),
                _ => throw new InvalidOperationException(
                    "Subject field decryptor requires both IAuditTrail and IOperationSigner registered, or neither. "
                    + $"Mid-state misconfiguration: IAuditTrail={(auditTrail is null ? "null" : "registered")}, "
                    + $"IOperationSigner={(signer is null ? "null" : "registered")}.")
            };
        });

        // The erasure service requires an audited path — it registers ONLY when an
        // IAuditTrail + IOperationSigner are present (an unaudited erasure is not
        // permitted). Hosts without the audit substrate wired do not get the
        // service and therefore cannot perform an erasure.
        services.TryAddSingleton<ISubjectErasureService>(sp =>
            new SubjectErasureService(
                sp.GetRequiredService<ISubjectErasureRegistry>(),
                sp.GetRequiredService<ISubjectTombstoneStore>(),
                sp.GetRequiredService<IAuditTrail>(),
                sp.GetRequiredService<IOperationSigner>(),
                sp.GetRequiredService<ITenantKeyDestroyer>(),
                minimumWindow: null,
                clock: sp.GetRequiredService<IRecoveryClock>(),
                // F1 (Slice-1d) — derived-cleartext-cache purge sinks. A host that maintains a derived
                // cleartext cache of subject ciphertext (the KG-search vec0 acceleration table) registers an
                // ISubjectErasurePropagator; the erasure flow then drops that residue on shred. Resolved as a
                // collection so it is EMPTY by default (no derived caches) and additive per registered sink.
                propagators: sp.GetServices<ISubjectErasurePropagator>(),
                // ADR 0142 — the non-bypassable legal-hold gate. Injected here so EVERY erasure
                // (manual GDPR + scheduled retention-shred, which routes through this same service)
                // consults it. GetService (not GetRequiredService): a host that never registers holds
                // gets null and the legacy un-gated behaviour; the coordinator's own TryAdd above means
                // a normally-composed host always has at least the empty default registry.
                holds: sp.GetService<ILegalHoldRegistry>()));

        return services;
    }

    /// <summary>
    /// Fail-closed durable-store gate for GDPR subject erasure (ADR 0135 GDPR
    /// direction; ADR 0068 §1.3). Throws <see cref="InvalidOperationException"/>
    /// unless BOTH <see cref="ISubjectErasureRegistry"/> and
    /// <see cref="ISubjectTombstoneStore"/> are registered with a NON-volatile
    /// implementation — i.e. the restart-volatile in-memory defaults
    /// (<see cref="InMemorySubjectErasureRegistry"/> /
    /// <see cref="InMemorySubjectTombstoneStore"/>) have been overridden. A host
    /// that wires the erasure subsystem for production MUST call this at its
    /// composition root after registering its durable, append-only stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a structural gate, not a comment.</b> The erasure registry
    /// and tombstone store are <em>compliance records</em>: the tombstone records the
    /// decision, and the registry prevents replacement-key creation after the stored
    /// subject key is deleted. If either resolves to the in-memory default, a restart
    /// forgets the erasure and permits new writes for that identity. Surfacing the
    /// volatility only in XML-doc prose left a restart-volatile
    /// default one missed override away from production; this turns that into a
    /// deterministic startup failure (earlier repository ticket #1351 deep-review MAJOR).
    /// </para>
    /// <para>
    /// <b>What counts as durable.</b> The gate is conservative: it inspects the
    /// registered service descriptor and rejects ONLY the two known in-memory
    /// default types. Any other implementation — a different concrete type, a
    /// pre-built instance, or a factory registration — is accepted as the host's
    /// deliberate, durable choice. The host owns the append-only / non-volatile
    /// guarantee of whatever it wires (the gate cannot prove durability of an
    /// arbitrary type; it closes the silent-default hole, which is the actual risk).
    /// Register the durable stores BEFORE <see cref="AddHarborlineRecoveryCoordinator"/>
    /// (whose <c>TryAddSingleton</c> then keeps them) or replace the descriptors
    /// after; either way, call this gate last.
    /// </para>
    /// <para>
    /// The check runs against the <see cref="IServiceCollection"/> at composition
    /// time, so it cannot be defeated by lazy resolution and does not build the
    /// provider or instantiate any store.
    /// </para>
    /// </remarks>
    /// <param name="services">The composition-root service collection to validate.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Either erasure store is unregistered or still resolves to its
    /// restart-volatile in-memory default.
    /// </exception>
    public static IServiceCollection RequireDurableErasureStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AssertDurable(services, typeof(ISubjectErasureRegistry), typeof(InMemorySubjectErasureRegistry));
        AssertDurable(services, typeof(ISubjectTombstoneStore), typeof(InMemorySubjectTombstoneStore));

        return services;
    }

    /// <summary>
    /// Inspects the last registration for <paramref name="serviceType"/> and throws
    /// fail-closed if it is missing or still the restart-volatile
    /// <paramref name="volatileDefaultType"/>. "Last" matches DI resolution
    /// semantics — the most recently added descriptor for a type is what resolves.
    /// </summary>
    private static void AssertDurable(IServiceCollection services, Type serviceType, Type volatileDefaultType)
    {
        // Walk from the end: the effective (resolved) registration for a service
        // type is the LAST descriptor added for it.
        ServiceDescriptor? effective = null;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == serviceType)
            {
                effective = services[i];
                break;
            }
        }

        if (effective is null)
        {
            throw new InvalidOperationException(
                $"GDPR erasure durable-store gate: no {serviceType.Name} is registered. "
                + "A host wiring subject erasure MUST register a durable, append-only "
                + $"{serviceType.Name} (the tombstone/registry is a compliance record per ADR 0068 §1.3) "
                + "before calling RequireDurableErasureStores().");
        }

        // Only the implementation TYPE distinguishes the volatile default. An
        // instance or factory registration is, by construction, NOT the default
        // TryAddSingleton<TInterface, TImpl>() and is treated as the host's
        // deliberate durable wiring.
        if (effective.ImplementationType == volatileDefaultType)
        {
            throw new InvalidOperationException(
                $"GDPR erasure durable-store gate: {serviceType.Name} still resolves to the "
                + $"restart-volatile in-memory default ({volatileDefaultType.Name}). A lost "
                + "registry/tombstone forgets the erasure decision and permits replacement-key creation. "
                + "Register a durable, append-only implementation (the same non-volatile "
                + "substrate that owns the audit trail) before calling RequireDurableErasureStores().");
        }
    }
}
