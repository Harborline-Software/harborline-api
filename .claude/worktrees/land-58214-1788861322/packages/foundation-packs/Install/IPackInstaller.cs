using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// The Pack Composer install engine (B-1b) — the security-concentrated half. Turns a VERIFIED pack file
/// into an immutable seed layer under a single atomic transaction, re-attaching prior tenant overrides,
/// enforcing the S-8 monotonic version + floor watermark, running the FULL ADR 0143 admission over the
/// composed cascade (S-9), and auditing every mutation. Only a <c>Verified</c> verdict admits (S-7):
/// there is no install path that bypasses verification.
/// </summary>
public interface IPackInstaller
{
    /// <summary>
    /// Computes the install PLAN without mutating anything (the D8 install-preview): WHO signed, WHAT
    /// would change (new seeds, the upgrade version pair), the re-attach CONFLICT report, S-8 watermark
    /// hits, revocation staleness, and admission refusals.
    /// </summary>
    PackInstallPreview Preview(ReadOnlySpan<byte> packBytes, PackInstallContext context);

    /// <summary>
    /// Installs a VERIFIED pack: verify-gate → revocation → scope → projector support → S-8 watermark →
    /// ADR 0143 admission → S-10 re-attach → ATOMIC seed-layer commit → durable audit. The seed layer is created in
    /// <see cref="PackLifecycleState.Draft"/>; call <see cref="Activate(PackInstallContext, string, string)"/> to make it live. A watermark
    /// refusal (downgrade / floor-weakening) proceeds ONLY with a break-glass ceremony on the context.
    /// </summary>
    PackInstallOutcome Install(ReadOnlySpan<byte> packBytes, PackInstallContext context);

    /// <summary>
    /// Activates an installed version (ADR 0011 Draft/Inactive → Active pointer flip) + audits it. A prior Active
    /// version is Superseded but its immutable seed layer is retained (S-2). The pointer flip makes seed
    /// content LIVE, so the domain layer requires the authority carried by <see cref="PackInstallContext"/>.
    /// </summary>
    PackActivationOutcome Activate(PackInstallContext context, string packKey, string version);

    /// <summary>
    /// Deactivates the named Active version through a reversible pointer flip. No seed layer, tenant override,
    /// ownership choice, watermark, or tenant-authored data is deleted. Requires
    /// authority through the same compiled context as installation and activation.
    /// </summary>
    PackDeactivationOutcome Deactivate(PackInstallContext context, string packKey, string version);

    /// <summary>
    /// Records an administrator's NARROWING of one content key of the pack's Active version (ticket 208
    /// L624) as the ORDINARY tenant override — the same RFC-7396 overlay row the S-10 re-attach carries
    /// across an upgrade, read back by every projection pass. An overlay that would WIDEN the reviewed
    /// seed (add a field, add a state, add a role) is refused with
    /// <see cref="Merge.PackTenantNarrowing.WideningRefusedCode"/> and nothing is stored. This does not
    /// project: the next pass applies the recorded overlay, which is also what makes it survive replay.
    /// The caller supplies its guard's decision for this pack, tenant, principal, and instant; the
    /// installer validates and carries that same object to the audit without re-deciding.
    /// </summary>
    PackNarrowingOutcome Narrow(
        PackInstallContext context, string packKey, string contentKey, System.Text.Json.Nodes.JsonNode overlayPatch,
        AuthorizationDecision decision);
}
