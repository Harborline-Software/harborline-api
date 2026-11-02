using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;

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
    /// Runs admission as an authoring check. Unlike <see cref="Preview"/>, this reports every
    /// non-terminal refusal that can be evaluated from one verified pack, and never commits a seed layer.
    /// </summary>
    PackInstallPreview Check(ReadOnlySpan<byte> packBytes, PackInstallContext context);

    /// <summary>
    /// Installs a VERIFIED pack: verify-gate → revocation → scope → projector support → S-8 watermark →
    /// ADR 0143 admission → S-10 re-attach → ATOMIC seed-layer commit → durable audit. The seed layer is created in
    /// <see cref="PackLifecycleState.Draft"/> only for a new coordinate; an identical retry preserves its existing
    /// lifecycle, seeds, overlays and projection state. Call <see cref="ActivateAsync(PackInstallContext, string, string, CancellationToken)"/> to make it live. A watermark
    /// refusal (downgrade / floor-weakening) proceeds ONLY with a break-glass ceremony on the context.
    /// </summary>
    Task<PackInstallOutcome> InstallAsync(
        ReadOnlyMemory<byte> packBytes, PackInstallContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates an installed version (ADR 0011 Draft/Inactive → Active pointer flip) + audits it. A prior Active
    /// version is Superseded but its immutable seed layer is retained (S-2). The pointer flip makes seed
    /// content LIVE, so the domain layer requires the authority carried by <see cref="PackInstallContext"/>.
    /// </summary>
    Task<PackActivationOutcome> ActivateAsync(
        PackInstallContext context, string packKey, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates the named Active version through a reversible pointer flip. No seed layer, tenant override,
    /// ownership choice, watermark, or tenant-authored data is deleted. Requires
    /// authority through the same compiled context as installation and activation.
    /// </summary>
    Task<PackDeactivationOutcome> DeactivateAsync(
        PackInstallContext context, string packKey, string version, CancellationToken cancellationToken = default);

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
    Task<PackNarrowingOutcome> NarrowAsync(
        PackInstallContext context, string packKey, string contentKey, System.Text.Json.Nodes.JsonNode overlayPatch,
        AuthorizationDecision decision, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="InstallAsync(ReadOnlyMemory{byte}, PackInstallContext, CancellationToken)"/> staged into
    /// <paramref name="transaction"/>, which the caller opened on this flow and alone commits. Every stage still
    /// authorizes, binds and validates as the standalone write does; success is published only after that commit,
    /// and refusal audit only after confirmed resource release and the owner's ReactAsync. A valid attempted
    /// refusal, fault or cancellation irreversibly prevents Commit. Only the owner disposes and reacts;
    /// disposal attempts do not prove rollback or release. An installer that cannot join marks the attempt failed.
    /// </summary>
    Task<PackInstallOutcome> InstallAsync(
        ReadOnlyMemory<byte> packBytes, PackInstallContext context, PackProjectionTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.BeginJoinedInstall();
        var failure = new InvalidOperationException($"Pack installer '{GetType().FullName}' cannot join a projection transaction.");
        transaction.MarkFailed(failure);
        throw failure;
    }

    /// <summary><see cref="ActivateAsync(PackInstallContext, string, string, CancellationToken)"/> staged into
    /// <paramref name="transaction"/> with the standalone authorization, binding and admission stages. The caller alone
    /// owns Commit, Dispose and ReactAsync and must abandon a refused, faulted or cancelled attempt. This overload
    /// retains its prior failure rules, without joined install's automatic poisoning; existing lifecycle guards
    /// still reject work on an already failed transaction.</summary>
    Task<PackActivationOutcome> ActivateAsync(
        PackInstallContext context, string packKey, string version, PackProjectionTransaction transaction,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"Pack installer '{GetType().FullName}' cannot join a projection transaction.");

    /// <summary><see cref="NarrowAsync(PackInstallContext, string, string, System.Text.Json.Nodes.JsonNode, AuthorizationDecision, CancellationToken)"/>
    /// staged into <paramref name="transaction"/> with the standalone authorization, binding and admission stages.
    /// A later narrowing of the same pack binds and admits over the overlays already staged there. The caller alone
    /// owns Commit, Dispose and ReactAsync and must abandon a refused, faulted or cancelled attempt. This overload
    /// retains its prior failure rules, without joined install's automatic poisoning; existing lifecycle guards
    /// still reject work on an already failed transaction.</summary>
    Task<PackNarrowingOutcome> NarrowAsync(
        PackInstallContext context, string packKey, string contentKey, System.Text.Json.Nodes.JsonNode overlayPatch,
        AuthorizationDecision decision, PackProjectionTransaction transaction, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"Pack installer '{GetType().FullName}' cannot join a projection transaction.");
}
