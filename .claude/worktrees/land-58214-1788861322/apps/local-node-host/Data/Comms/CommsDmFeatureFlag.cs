using Harborline.OperationalEnvironment;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The SHIPPED feature flag for the direct-message route surface — default <b>ON</b>, with a kill-switch.
/// </summary>
/// <remarks>
/// <para>
/// <b>History.</b> This was the C2 <c>CommsDmDevGate</c> — a pre-encryption DEV/TEST fence that defaulted OFF so a
/// PLAINTEXT DM (C2) could never reach a user. The DM body is now SEALED end-to-end: C4 added the content seal
/// (X25519-ECDH+HKDF, sign-then-encrypt, AAD, ChaCha20-Poly1305) and C5 made the keys roster-bound and node-secret
/// (a non-participant — even one LYING about its party id — gets only ciphertext; the leak property is enforced by
/// real keys, proven in CI, and re-verified across three sec-eng deep-review rounds). With confidentiality
/// delivered AND post-C5 cross-machine enrollment wire-verified, CIC approved exposing DMs (2026-06-22, ADR 0136).
/// So the dev-gate becomes a shipped feature flag: <b>default ON</b> (the DM surface ships), with a kill-switch the
/// operator can flip to disable the surface in an incident — WITHOUT a redeploy and without touching crypto.
/// </para>
/// <para>
/// <b>What the flag covers.</b> It fences the DM-specific resolve-and-open route
/// (<c>POST/GET /api/local-node/comms/dm/{otherPartyId}</c> — derive the deterministic <c>dm:</c> id server-side,
/// open the keyed thread). The team channel + the generic C1 conversation-addressed routes
/// (<c>/api/local-node/comms/{conversationId}</c>) are UNAFFECTED. Disabling the flag removes ONLY the DM route;
/// team messaging is unchanged.
/// </para>
/// <para>
/// <b>Default: ON (shipped). Kill-switch: <c>HARBORLINE_COMMS_DM_DISABLED</c>.</b> The flag is enabled unless the
/// operator explicitly sets <c>HARBORLINE_COMMS_DM_DISABLED</c> to a truthy value (<c>1</c> / <c>true</c> /
/// <c>yes</c> / <c>on</c>, case-insensitive) — the incident kill-switch. The explicit-state constructor
/// (<see cref="CommsDmFeatureFlag(bool)"/>) is the path tests + the cross-machine DM harness use to set the surface
/// deterministically (independent of the ambient environment).
/// </para>
/// <para>
/// <b>The kill-switch is NOT a confidentiality boundary.</b> DM confidentiality rests on the C5 roster-bound,
/// node-secret crypto — NOT on this flag. Disabling the flag removes the convenience route a user uses to OPEN a
/// DM; it does not (and must not be relied upon to) protect message bodies — those are sealed at rest regardless.
/// This is the inverse of the old C2/C4 posture where the gate was load-bearing for confidentiality (the B1
/// lesson, PR #1325): it is safe to ship the surface ON precisely because the crypto — not the route gate — is
/// what stands between a non-participant and a plaintext body.
/// </para>
/// </remarks>
public sealed class CommsDmFeatureFlag
{
    /// <summary>The kill-switch environment variable — set truthy to DISABLE the shipped DM surface.</summary>
    public const string DisableEnvVarName = "HARBORLINE_COMMS_DM_DISABLED";

    /// <summary>True when the DM route surface is enabled (the shipped default; false only when killed).</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Constructs the flag with an explicit enabled state — the path tests + the cross-machine DM harness use to
    /// set the DM surface deterministically (independent of the ambient environment).
    /// </summary>
    public CommsDmFeatureFlag(bool enabled) => IsEnabled = enabled;

    /// <summary>
    /// Constructs the flag from the ambient environment — enabled (the shipped default) UNLESS the kill-switch
    /// <see cref="DisableEnvVarName"/> is set to a truthy value. The shipped host uses this overload (and does not
    /// set the var), so the DM surface is ON in production.
    /// </summary>
    public CommsDmFeatureFlag()
        : this(!IsTruthy(HarborlineOperationalEnvironment.Read(DisableEnvVarName)))
    {
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
