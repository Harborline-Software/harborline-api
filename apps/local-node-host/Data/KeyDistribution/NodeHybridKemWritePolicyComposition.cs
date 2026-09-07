using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.OperationalEnvironment;

namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// PQC Phase 2 / BL-01 increment <b>2c-iii-c — the host-composition ENABLE of the hybrid-write cutover</b>
/// (ADR 0004 Amendment 2 GATE condition 5). The host wiring that turns the 2c-iii-b writer flip ON: it OVERRIDES
/// kernel-security's fail-closed <see cref="DisabledHybridKemWritePolicy"/> default with an enabled
/// <see cref="ConfigurableHybridKemWritePolicy"/> so production starts boxing the hybrid suite #3 (standard X-Wing)
/// for X-Wing-capable recipients — and keeps the disabled policy as the revertible kill-switch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default; an explicit host act turns it on.</b> kernel-security registers
/// <see cref="DisabledHybridKemWritePolicy"/> via <c>TryAdd</c> (suite #1 only — the safe pre-cutover state).
/// <see cref="AddNodeHybridKemWritePolicy"/> <see cref="ServiceCollectionDescriptorExtensions.Replace">Replace</see>s
/// it with a <see cref="ConfigurableHybridKemWritePolicy"/> whose enabled state the host computes from its config
/// (the per-install opt-in / canary flag) AND a no-redeploy env kill-switch. This mirrors how
/// <c>AddRosterBoundTenantDekPairingResolver</c> deposes the fail-closed pairing default, and how the comms DM
/// feature flag layers a config default under an env kill-switch.
/// </para>
/// <para>
/// <b>Staged rollout (local-first).</b> The fleet is per-install hosts, so the rollout knob is the per-install
/// config flag <c>LocalNode:HybridKemWrite:Enabled</c>: turn the cutover ON for a canary cohort of installs first,
/// observe the GATE-6 offline round-trip + unwrap health, then widen. The default is OFF, so an install that has
/// NOT been opted in stays on the safe suite-#1 behaviour — there is no all-installs-on-this-build flip.
/// </para>
/// <para>
/// <b>The kill-switch (GATE condition 5).</b> Two ways to revert to suite #1 with NOTHING stranded (the read path,
/// #1488/2c-iii-a, keeps accepting already-emitted suite-#3 boxes):
/// <list type="number">
///   <item>flip the config flag <c>LocalNode:HybridKemWrite:Enabled</c> back to <c>false</c> and restart; or</item>
///   <item>set the env kill-switch <c>HARBORLINE_PQC_HYBRID_WRITE_DISABLED</c> truthy — this FORCES the policy OFF
///         even when config enables it, so an incident responder can revert WITHOUT a config change / redeploy.</item>
/// </list>
/// Either path returns the writer to suite #1 on the next write; previously-emitted suite-#3 boxes stay openable.
/// </para>
/// </remarks>
public static class NodeHybridKemWritePolicyComposition
{
    /// <summary>
    /// The incident kill-switch environment variable. Set truthy (<c>1</c> / <c>true</c> / <c>yes</c> / <c>on</c>,
    /// case-insensitive) to FORCE the hybrid-write cutover OFF (suite #1 only) even when config enables it — the
    /// no-redeploy revert. Mirrors <c>CommsDmFeatureFlag.DisableEnvVarName</c>.
    /// </summary>
    public const string DisableEnvVarName = "HARBORLINE_PQC_HYBRID_WRITE_DISABLED";

    /// <summary>
    /// Override kernel-security's fail-closed <see cref="DisabledHybridKemWritePolicy"/> default with an enabled
    /// <see cref="ConfigurableHybridKemWritePolicy"/> driven by the host config flag — the 2c-iii-c cutover enable.
    /// The effective enabled state is <paramref name="configEnabled"/> AND NOT the env kill-switch: the config flag
    /// is the per-install opt-in, the env var is the incident force-off that ALWAYS wins. When the result is
    /// <c>false</c> this still Replaces the default with a disabled <see cref="ConfigurableHybridKemWritePolicy"/>
    /// (functionally identical to the fail-closed default — suite #1 only), so the wiring is uniform and the kill
    /// path is exercised by the same code.
    /// </summary>
    /// <param name="services">The DI container to register into.</param>
    /// <param name="configEnabled">
    /// The host config flag (<c>LocalNode:HybridKemWrite:Enabled</c>) — the per-install opt-in / canary flag.
    /// </param>
    public static IServiceCollection AddNodeHybridKemWritePolicy(
        this IServiceCollection services,
        bool configEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The env kill-switch ALWAYS wins: even if config opted this install into the cutover, a truthy
        // HARBORLINE_PQC_HYBRID_WRITE_DISABLED forces the writer back to suite #1 with no config change / redeploy.
        var killSwitchEngaged = IsTruthy(HarborlineOperationalEnvironment.Read(DisableEnvVarName));
        var effectiveEnabled = configEnabled && !killSwitchEngaged;

        // Replace (not TryAdd) so this deterministically deposes kernel-security's TryAdd'd
        // DisabledHybridKemWritePolicy default. A disabled ConfigurableHybridKemWritePolicy is functionally
        // identical to the fail-closed default (HybridWritesEnabled == false → the writer boxes suite #1), so the
        // off path is the same code as the on path with one flag flipped — no hidden second behaviour.
        services.Replace(ServiceDescriptor.Singleton<IHybridKemWritePolicy>(
            new ConfigurableHybridKemWritePolicy(effectiveEnabled)));

        return services;
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
