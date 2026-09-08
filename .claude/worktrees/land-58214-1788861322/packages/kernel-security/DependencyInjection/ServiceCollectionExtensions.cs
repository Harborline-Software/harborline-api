using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Security.Attestation;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Security.Verification;

namespace Harborline.Api.Kernel.Security.DependencyInjection;

/// <summary>
/// Registers the kernel-security surface (Ed25519/X25519 primitives, attestation
/// issuer+verifier, role-key manager) in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Harborline.Api.Kernel.Security services as singletons.
    /// Callers must also register an <see cref="IKeystore"/> separately — the
    /// platform-appropriate one can be obtained via
    /// <c>IKeystore keystore = Keystore.CreateForCurrentPlatform();</c>.
    /// </summary>
    public static IServiceCollection AddHarborlineKernelSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEd25519Signer, Ed25519Signer>();
        services.AddSingleton<IX25519KeyAgreement, X25519KeyAgreement>();
        // PQC Phase 2 / BL-01 (ADR 0004 §1, KEM suite #2) — ML-KEM-768 primitive + the X25519‖ML-KEM
        // hybrid sealed box. Registered as available substrate; production wrappers still box as suite #1
        // (the recipient ML-KEM key model + policy-gated rollout is Phase 2c).
        services.AddSingleton<IMlKem768, MlKem768>();
        services.AddSingleton<IHybridSealedBox, HybridSealedBox>();
        // PQC Phase 2 / BL-01 (ADR 0004 Amendment 2, KEM suite #3, CIC decision D1) — the standardized
        // IETF/CFRG X-Wing KEM (X25519 + ML-KEM-768) + its sealed box: the PRODUCTION hybrid construction.
        // Registered as available substrate; production wrappers STILL box as suite #1 — the recipient
        // X-Wing key-model + policy-gated cutover is increment 2c-iii, the eager re-box sweep is 2c-ii.
        services.AddSingleton<IXWingKem, XWingKem>();
        services.AddSingleton<IXWingSealedBox, XWingSealedBox>();
        services.AddSingleton<IAttestationIssuer, AttestationIssuer>();
        services.AddSingleton<IAttestationVerifier, AttestationVerifier>();
        services.AddSingleton<IRoleKeyManager, RoleKeyManager>();
        services.AddSingleton<ITeamSubkeyDerivation, TeamSubkeyDerivation>();
        services.AddSingleton<ISqlCipherKeyDerivation, SqlCipherKeyDerivation>();
        // W#67 / ADR 0046-A6 — per-team X25519 subkey for trustee
        // seed-envelope encryption. Distinct HKDF prefix from the Ed25519
        // and SQLCipher derivations; see HkdfX25519SubkeyDerivation.InfoPrefix.
        services.AddSingleton<IX25519SubkeyDerivation, HkdfX25519SubkeyDerivation>();
        // PQC Phase 2 / BL-01 (ADR 0004 Amendment 2 GATE 4a, increment 2c-iii — read-before-write) — per-team
        // X-Wing subkey. HKDF-derives the recipient's 32-byte X-Wing private-key seed from the install root seed
        // (distinct prefix "sunfish-xwing-team-v1:"), expands its public half via the registered IXWingKem. This
        // is what makes a recipient X-Wing-CAPABLE so the production READ paths below can open a suite-#3 box
        // BEFORE any writer emits one (anti-stranding). Same custody model as every root-derived key — no new
        // custodian, no new persistence; the OS-keystore root seed stays the sole custody root.
        services.AddSingleton<IXWingSubkeyDerivation, HkdfXWingSubkeyDerivation>();
        // PQC Phase 2 / BL-01 increment 2c-iii-b (ADR 0004 Amendment 2 GATE condition 5) — the hybrid-write
        // policy (the cutover KILL-SWITCH) that gates whether the writers emit suite #3. Registered FAIL-CLOSED by
        // default (DisabledHybridKemWritePolicy → the pre-cutover state, suite #1 only). A host turns the cutover
        // ON by OVERRIDING this registration with a policy that returns true (e.g. a config/env-driven
        // ConfigurableHybridKemWritePolicy); flipping it back to disabled reverts the writers to suite #1 with
        // nothing stranded (the read path keeps accepting #3). TryAdd keeps the FIRST registration, so
        // the host's policy wins only if it is registered BEFORE this extension is called.
        services.TryAddSingleton<IHybridKemWritePolicy>(DisabledHybridKemWritePolicy.Instance);
        // MD-1 (joint ADR 0113+0117 amendment) — the tenant-DEK pairing CONSTRUCTION:
        // encrypt-then-sign + context-binding over the 0046-A6 X25519 sealed box. The single
        // TenantDekWrapper serves both the wrap (ITenantDekWrapper) and verify+unwrap
        // (ITenantDekUnwrapper) sides. This is the construction ONLY — the roster-bound
        // recipient-key resolver + the no-mock-crypto arch-fence (G-1) live in the host.
        // The container injects the registered IXWingSealedBox + IHybridKemWritePolicy (above) into the optional
        // ctor parameters, so the unwrap path READS a suite-#3 wrap (2c-iii-a) AND — as of 2c-iii-b — the WRITE
        // path EMITS suite #3 for an X-Wing-capable recipient WHEN the policy is enabled (default disabled, so the
        // composed-here writer stays suite-#1-only until a host turns the kill-switch on).
        services.AddSingleton<TenantDekWrapper>();
        services.AddSingleton<ITenantDekWrapper>(sp => sp.GetRequiredService<TenantDekWrapper>());
        services.AddSingleton<ITenantDekUnwrapper>(sp => sp.GetRequiredService<TenantDekWrapper>());

        // ADR 0152 Card B — the shared-verification-secret seam (SAS safety-code source). PRODUCTION
        // registers the FAIL-CLOSED null-object by default (mirrors ADR 0136 NoDmConversationKeyProvider):
        // a minimal graph with no authenticated engagement context degrades to "verification unavailable",
        // NEVER to an identity-derivable stand-in (the no-mock-crypto arch-fence, F3). A consumer that holds
        // a real authenticated context (a completed ADR 0076 handshake or a purpose-signed co-roster ECDH)
        // constructs HandshakeTranscriptSecretSource / RosterEcdhSecretSource explicitly with the runtime
        // secret material. TryAdd keeps the FIRST registration, so a host override wins only when it
        // registers its own ISharedVerificationSecretSource BEFORE calling this extension; registering
        // after this call is a no-op and silently leaves the null-object in place.
        services.TryAddSingleton<ISharedVerificationSecretSource, NoSharedVerificationSecretSource>();

        return services;
    }

    /// <summary>
    /// Registers the keystore-backed <see cref="IRootSeedProvider"/> for this
    /// install. Ensures an <see cref="IKeystore"/> is registered (via
    /// <see cref="Keystore.CreateForCurrentPlatform(string?)"/>) and registers
    /// <see cref="KeystoreRootSeedProvider"/> as the
    /// <see cref="IRootSeedProvider"/> implementation. Both registrations are
    /// singleton and use <c>TryAdd</c>, so tests (or bridge/anchor composition
    /// roots) can override either service by registering their own
    /// <see cref="IKeystore"/> or <see cref="IRootSeedProvider"/> before
    /// calling this extension (e.g., <see cref="InMemoryKeystore"/> for
    /// deterministic per-test seeds).
    /// </summary>
    /// <param name="services">The DI container to register into.</param>
    /// <param name="keystoreStorageDirectory">
    /// Optional override for the platform keystore's on-disk storage
    /// directory. On Windows this backs the DPAPI ciphertext file; defaults to
    /// <c>%LOCALAPPDATA%/Sunfish/keys</c>. macOS and Linux stubs currently
    /// throw <see cref="PlatformNotSupportedException"/> on first use (Wave-2
    /// follow-up).
    /// </param>
    /// <param name="installIdentityFilePath">
    /// Installer-owned identity record retained across restarts and upgrades. When omitted, a
    /// user-writable path is derived from the stable installation directory by
    /// <see cref="InstallIdentityPaths.GetDefaultIdentityFilePath(string?)"/>.
    /// </param>
    public static IServiceCollection AddHarborlineRootSeedProvider(
        this IServiceCollection services,
        string? keystoreStorageDirectory = null,
        string? installIdentityFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHarborlineInstallFootprint(installIdentityFilePath: installIdentityFilePath);
        services.TryAddSingleton<IKeystore>(sp =>
        {
            var footprint = sp.GetRequiredService<IInstallFootprintProvider>()
                .GetInstallFootprintAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return Keystore.CreateForCurrentPlatform(
                keystoreStorageDirectory ?? footprint.KeystoreDirectory);
        });
        services.TryAddSingleton<KeystoreRootSeedProvider>();
        services.TryAddSingleton<IRootSeedProvider>(
            sp => sp.GetRequiredService<KeystoreRootSeedProvider>());
        // W#67 — IRootSeedRestorer single-use write path is implemented by
        // the same KeystoreRootSeedProvider singleton so a restore observed
        // by the restorer is immediately visible to the provider's cache
        // (the restorer invalidates the provider's Lazy<>). Only
        // AnchorRecoveryCompletionHandler should inject IRootSeedRestorer.
        services.TryAddSingleton<IRootSeedRestorer>(
            sp => sp.GetRequiredService<KeystoreRootSeedProvider>());

        return services;
    }

    // AddHarborlineRecoveryCoordinator moved to
    // packages/foundation-recovery/DependencyInjection/ServiceCollectionExtensions.cs
    // per ADR 0046 package-placement amendment. Recovery orchestration is
    // foundation-tier; kernel-security only owns the crypto primitives it
    // depends on.
}
