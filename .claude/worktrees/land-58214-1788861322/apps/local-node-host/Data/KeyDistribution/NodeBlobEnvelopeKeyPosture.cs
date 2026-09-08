using System;
using System.Collections.Generic;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// PERS-1 fail-closed KEY POSTURE gate for the storage-role envelope blob store (ADR 0137 D4; the no-mock-crypto
/// family, bug-1312 / #1325). The blob envelope's confidentiality rests entirely on the
/// <see cref="ITenantKeyProvider"/> being the REAL, install-secret provider (the node's
/// <see cref="StoredTenantKeyProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>PERS-2 F1 — this is now an ALLOWLIST, not a denylist-of-one.</b> The PERS-1 gate rejected only the ONE known
/// dev stub (<see cref="InMemoryTenantKeyProvider"/>); ANY other derivable / stand-in / test-double /
/// not-yet-imagined provider slipped through (the PERS-1 deep-review F1 finding). The gate now PASSES only when the
/// effective <see cref="ITenantKeyProvider"/> is a KNOWN-REAL provider type on <see cref="KnownRealProviderTypes"/>
/// (the hierarchy-wrapped <see cref="StoredTenantKeyProvider"/>); everything else — the dev stub, an unknown
/// concrete type, or an opaque factory whose type cannot be introspected — fails closed. A new confidentiality-safe
/// provider is admitted by ADDING it to the allowlist, a deliberate act, never by default.
/// </para>
/// <para>
/// Only an INSTANCE or a concrete TYPE registration can be verified against the allowlist. A factory registration
/// (<c>AddSingleton&lt;ITenantKeyProvider&gt;(sp =&gt; …)</c>) is opaque at composition time, so it is REFUSED
/// fail-closed — the host's real wiring is an instance registration
/// (<c>AddSingleton&lt;ITenantKeyProvider&gt;(new RootSeedTenantKeyProvider(seed))</c>), which IS verifiable. This
/// composition-time gate is paired with the storage-role arch-fence, which asserts the same posture over the REAL
/// host composition graph (PERS-2 F4).
/// </para>
/// </remarks>
public static class NodeBlobEnvelopeKeyPosture
{
    /// <summary>
    /// The ALLOWLIST of confidentiality-safe, install-secret <see cref="ITenantKeyProvider"/> implementations. Only
    /// these may back the storage-role blob envelope. Adding a type here is a deliberate confidentiality decision.
    /// </summary>
    public static readonly IReadOnlySet<Type> KnownRealProviderTypes = new HashSet<Type>
    {
        typeof(RootSeedTenantKeyProvider),
        typeof(StoredTenantKeyProvider),
    };

    /// <summary>
    /// Throw fail-closed unless the effective (last-registered) <see cref="ITenantKeyProvider"/> is a KNOWN-REAL
    /// provider on <see cref="KnownRealProviderTypes"/>. Call from the composition root AFTER the real provider is
    /// registered and the recovery substrate's <c>TryAddSingleton&lt;ITenantKeyProvider, InMemoryTenantKeyProvider&gt;</c>
    /// default has been added, BEFORE the storage-role blob store is resolved.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No tenant-key provider is registered, or the effective one is not an allowlisted real provider (the dev stub,
    /// an unknown concrete type, or an unintrospectable factory).
    /// </exception>
    public static IServiceCollection RequireRealBlobEnvelopeKeyProvider(this IServiceCollection services)
        => RequireRealTenantKeyProvider(services, "Blob-envelope key-posture gate", "storage-role envelope blob store");

    /// <summary>
    /// The generalized last-descriptor + concrete-type-allowlist gate (PERS-2 F1 shape) — usable by
    /// any composition cell whose at-rest confidentiality rides the effective
    /// <see cref="ITenantKeyProvider"/> (the blob envelope, the CP-4 spatial-frame PII sealing).
    /// Throws fail-closed unless the LAST-registered provider is a verifiable instance or concrete
    /// type on <see cref="KnownRealProviderTypes"/>; a dev stub, an unknown type, or an opaque
    /// factory all refuse composition.
    /// </summary>
    /// <param name="services">The service collection under composition.</param>
    /// <param name="gateName">The failing gate's name, prefixed to every refusal message.</param>
    /// <param name="protectedCell">What the key protects — named in the refusal so the operator knows the blast radius.</param>
    /// <exception cref="InvalidOperationException">
    /// No tenant-key provider is registered, or the effective one is not an allowlisted real provider (the dev stub,
    /// an unknown concrete type, or an unintrospectable factory).
    /// </exception>
    public static IServiceCollection RequireRealTenantKeyProvider(
        this IServiceCollection services, string gateName, string protectedCell)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(gateName);
        ArgumentException.ThrowIfNullOrEmpty(protectedCell);

        // The effective registration for a service type is the LAST descriptor added for it (DI resolution semantics).
        ServiceDescriptor? effective = null;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ITenantKeyProvider))
            {
                effective = services[i];
                break;
            }
        }

        if (effective is null)
        {
            throw new InvalidOperationException(
                $"{gateName}: no ITenantKeyProvider is registered. The {protectedCell} "
                + "cannot seal at-rest data without an install-secret tenant-key provider (RootSeedTenantKeyProvider). "
                + $"Register it before wiring the {protectedCell}.");
        }

        // Resolve the effective registration's CONCRETE type (allowlist check). An instance or a type registration
        // is introspectable; a factory registration is opaque and therefore refused fail-closed.
        var concreteType = effective.ImplementationInstance?.GetType() ?? effective.ImplementationType;

        if (concreteType is null)
        {
            throw new InvalidOperationException(
                $"{gateName}: the ITenantKeyProvider is registered via an opaque factory whose "
                + $"concrete type cannot be verified at composition time. The {protectedCell} requires a "
                + "VERIFIABLE real provider — register the install-secret RootSeedTenantKeyProvider as an instance "
                + "(AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(seed))) so the allowlist can "
                + "confirm it.");
        }

        if (!KnownRealProviderTypes.Contains(concreteType))
        {
            throw new InvalidOperationException(
                $"{gateName}: the effective ITenantKeyProvider '{concreteType.FullName}' is not "
                + $"on the known-real allowlist. Sealing at-rest data for the {protectedCell} is permitted only under "
                + "an install-secret provider (RootSeedTenantKeyProvider); the dev InMemoryTenantKeyProvider (fixed "
                + "development salt — identical on every install) and any other stand-in derive keys recoverable by a "
                + "binary holder (the bug-1312 derivable-stand-in family). Register the real RootSeedTenantKeyProvider, "
                + "or add a genuinely confidentiality-safe provider to NodeBlobEnvelopeKeyPosture.KnownRealProviderTypes "
                + "deliberately.");
        }

        return services;
    }
}
