using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Catalog.Templates;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Verify;
namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// Default <see cref="IPackInstaller"/>. The pipeline runs in a fixed, fail-closed order so a security
/// gate always precedes any plan work: authorize → verify + bind (S-7) → revocation (S-11) → scope (S-13) → projector support →
/// S-8 watermark → ADR 0143 admission over the composed cascade (S-9) → S-10 total re-attach → ATOMIC
/// commit (S-7) → durable audit. Nothing reads a pack's content before <c>Verified</c>, and nothing mutates
/// a store before every gate has passed.
/// </summary>
public sealed class PackInstaller : IPackInstaller, IPackProjectionReconciler
{
    private static readonly IComparer<string> VersionComparer = Comparer<string>.Create(PackVersion.Compare);

    private readonly IPackVerifier _verifier;
    private readonly IPackInstallStore _store;
    private readonly IPackInstallMutationStore _mutations;
    private readonly IPackProjectionAdmissionStore? _projectionStore;
    private readonly IPackContentAdmission _admission;
    private readonly IPackInstallAudit _audit;
    private readonly IPackPlatformCompatibility _platform;
    private readonly AuthorizationGate _gate;
    private IPackProjectionDispatcher? _projector;

    /// <summary>Constructs the installer over the verifier + install store + admission port + audit sink.</summary>
    public PackInstaller(
        IPackVerifier verifier,
        IPackInstallStore store,
        IPackContentAdmission admission,
        IPackInstallAudit audit,
        AuthorizationGate gate,
        IPackPlatformCompatibility? platform = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mutations = store as IPackInstallMutationStore
            ?? throw new ArgumentException("The install reader must share one instance with its unregistered mutation face.", nameof(store));
        _projectionStore = store as IPackProjectionAdmissionStore;
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        // Deliberate ASYMMETRY with the projector seam: a null platform here becomes Empty, which
        // provides NO capability — any declared requirement then refuses, i.e. the ADMIT direction
        // fails CLOSED. PackSeedProjector keeps a null platform UNCHECKED instead, because its
        // fail-closed direction would retract already-admitted content on every back-compat embedder.
        _platform = platform ?? PackPlatformCompatibility.Empty;
    }

    /// <summary>Constructs a split reader/writer composition without recovering mutation faces by cast.</summary>
    public PackInstaller(
        IPackVerifier verifier,
        IPackInstallStore store,
        IPackInstallMutationStore mutations,
        IPackProjectionAdmissionStore projectionStore,
        IPackContentAdmission admission,
        IPackInstallAudit audit,
        AuthorizationGate gate,
        IPackPlatformCompatibility? platform = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _platform = platform ?? PackPlatformCompatibility.Empty;
    }

    public PackInstaller(
        IPackVerifier verifier,
        IPackInstallStore store,
        IPackInstallMutationStore mutations,
        IPackProjectionAdmissionStore projectionStore,
        IPackContentAdmission admission,
        IPackInstallAudit audit,
        AuthorizationGate gate,
        IPackProjectionDispatcher projector,
        IPackPlatformCompatibility? platform = null)
        : this(verifier, store, mutations, projectionStore, admission, audit, gate, platform)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    public PackInstaller(
        IPackVerifier verifier,
        IPackInstallStore store,
        IPackContentAdmission admission,
        IPackInstallAudit audit,
        AuthorizationGate gate,
        IPackProjectionDispatcher projector,
        IPackPlatformCompatibility? platform = null)
        : this(verifier, store, admission, audit, gate, platform)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    /// <inheritdoc />
    public PackInstallPreview Preview(ReadOnlySpan<byte> packBytes, PackInstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return BuildPlan(packBytes, context).Preview;
    }

    /// <inheritdoc />
    public PackInstallOutcome Install(ReadOnlySpan<byte> packBytes, PackInstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var claimed = ReadClaimedCoordinates(packBytes);

        if (string.IsNullOrWhiteSpace(context.Principal))
        {
            AuditPreDecisionRefusal(
                context.Tenant, claimed.PackKey, claimed.Version, context.Now, context.Principal,
                PackInstallCodes.RefusedNoPrincipal);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.Principal);
        }

        var decision = AuthorizeOrAudit(
            context.Tenant, context.Principal, context.Now, claimed.PackKey, claimed.Version);
        var plan = BuildPlan(packBytes, context, decision, claimed);
        var preview = plan.Preview;

        // Hard refusal (verify / revocation / scope / projector support / admission) — never overridable,
        // no mutation.
        if (preview.Verdict == PackInstallVerdict.Refused)
        {
            AuditRefused(context, preview, plan.SignerKeyId, decision);
            return new PackInstallOutcome(false, PackInstallAuditAction.Refused, preview.PackKey, preview.Version,
                preview.RefusalCodes, preview, BrokeGlass: false, decision);
        }

        // S-8 watermark refusal — proceeds ONLY under an explicit, audited break-glass ceremony.
        var brokeGlass = false;
        if (preview.Verdict == PackInstallVerdict.RequiresBreakGlass)
        {
            if (context.BreakGlass is null)
            {
                AuditRefused(context, preview, plan.SignerKeyId, decision);
                return new PackInstallOutcome(false, PackInstallAuditAction.Refused, preview.PackKey, preview.Version,
                    preview.RefusalCodes, preview, BrokeGlass: false, decision);
            }

            brokeGlass = true;
        }

        // ATOMIC apply (S-7): the seed layer + watermark + re-attached overrides commit all-or-nothing.
        var transaction = new PackInstallTransaction(
            context.Tenant, plan.NewInstalledPack!, plan.NewWatermark!, plan.Reattach!.Reattached);
        _mutations.Commit(transaction);

        // Durable audit. The break-glass ceremony is a DISTINCT, loud entry (S-8) recorded first.
        if (brokeGlass)
        {
            _audit.AppendAuthorized(new PackInstallAuditEntry(
                context.Tenant, PackInstallAuditAction.BreakGlassOverride, preview.PackKey, preview.Version,
                context.Now, plan.SignerKeyId, plan.Epoch,
                Detail: string.Join(",", preview.RefusalCodes),
                BreakGlassJustification: context.BreakGlass!.Justification,
                BreakGlassAuthorizingPrincipal: context.BreakGlass.AuthorizingPrincipal,
                ActingPrincipal: context.Principal), decision);
        }

        _audit.AppendAuthorized(new PackInstallAuditEntry(
            context.Tenant, plan.SuccessAction, preview.PackKey, preview.Version,
            context.Now, plan.SignerKeyId, plan.Epoch,
            Detail: plan.SuccessAction == PackInstallAuditAction.Upgraded ? PackInstallCodes.Upgraded : PackInstallCodes.Installed,
            ActingPrincipal: context.Principal), decision);

        return new PackInstallOutcome(
            true, plan.SuccessAction, preview.PackKey, preview.Version, Array.Empty<string>(), preview, brokeGlass, decision);
    }

    private PackActivationOutcome ActivateCore(
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string? actingPrincipal,
        IReadOnlyDictionary<string, string>? ownershipResolutions,
        out PackProjectionAuthority? projectionAuthority)
    {
        projectionAuthority = null;
        if (string.IsNullOrWhiteSpace(packKey))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.RefusedBlankPackKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.RefusedBlankVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        // Ticket 151 cluster: the Draft/Inactive → Active pointer flip is what makes seed content LIVE
        // — the more consequential mutation — so the domain layer requires the acting principal exactly
        // as Install does. Refused + audited, fail-closed.
        if (string.IsNullOrWhiteSpace(actingPrincipal))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivateRefusedNoPrincipal);
            ArgumentException.ThrowIfNullOrWhiteSpace(actingPrincipal);
        }

        var decision = AuthorizeOrAudit(tenant, actingPrincipal, now, packKey, version);

        foreach (var resolution in ownershipResolutions ?? new Dictionary<string, string>())
            _mutations.RecordKeyOwnership(tenant, resolution.Key, resolution.Value);

        // The target must be installed — fetch it FIRST so the activation guards (provider-slot,
        // cross-pack collision) inspect its persisted manifest state BEFORE any pointer flip.
        var target = _store.GetVersion(tenant, packKey, version);
        if (target is null)
        {
            return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivateNotInstalled, null, decision);
        }

        // Ticket 176: Access is a sibling released pack, but it may only become live after the
        // platform catalogue it relies on is live. Keep this at the admission point so direct
        // installer callers and the HTTP route receive the same named refusal as hosted preload.
        if (string.Equals(packKey, "harborline.access-administration", StringComparison.Ordinal)
            && _store.GetActive(tenant, "harborline.platform") is null)
        {
            return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivatePlatformPackRequired,
                "the released platform pack 'harborline.platform' must be active before "
                    + "'harborline.access-administration' can activate.", decision);
        }

        var unmetRequirements = PackPlatformRequirementCheck.FindUnmet(target, _platform);
        if (unmetRequirements.Count > 0)
        {
            var first = unmetRequirements[0];
            return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivateUnmetPlatformRequirement,
                $"capability '{first.Capability}' declared by '{first.DeclaredBy}' is unmet "
                    + $"({first.Failure}).", decision);
        }

        // (a) Provider-slot exclusivity (ADR 0129 D4 — ACTIVE-based). A category slot is "occupied" only
        //     by an ACTIVE provider of a DIFFERENT pack key. Install is additive (S-2: two providers may
        //     be installed, both Draft); exclusivity bites at ACTIVATE. Refuse (fail-closed) naming the
        //     incumbent — never silently supersede a live provider.
        if (!string.IsNullOrWhiteSpace(target.ProviderSlot))
        {
            var incumbent = _store.ListInstalled(tenant)
                .Where(p => p.Lifecycle == PackLifecycleState.Active
                    && !string.Equals(p.PackKey, packKey, StringComparison.Ordinal)
                    && string.Equals(p.ProviderSlot, target.ProviderSlot, StringComparison.Ordinal))
                .Select(p => p.PackKey)
                .FirstOrDefault();
            if (incumbent is not null)
            {
                return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.ActivateProviderSlotOccupied,
                    $"category slot '{target.ProviderSlot}' is already held by the active provider "
                        + $"pack '{incumbent}'; deactivate it before activating '{packKey}'.", decision);
            }
        }

        // (b) Cross-pack same-key collision (ADR 0129 D4/D5 — INSTALLED-based, the F4 gate). A content key
        //     this pack shares with ANOTHER installed pack must resolve to an owner (a declared dependency
        //     chain, or a recorded client choice) before EITHER goes live — else the projector would
        //     silently first-wins one. An UNRESOLVED shared key fails activation closed, naming the key +
        //     the other pack(s).
        var collisions = PackCompositionConflicts.Detect(
            PackCompositionConflicts.ClaimsFromInstalled(_store.ListInstalled(tenant)),
            _store.GetKeyOwnership(tenant));
        var unresolved = collisions.FirstOrDefault(c =>
            c.Resolution == PackKeyOwnershipResolution.RequiresChoice
            && c.ClaimingPackKeys.Contains(packKey, StringComparer.Ordinal));
        if (unresolved is not null)
        {
            var others = string.Join(", ", unresolved.ClaimingPackKeys.Where(k => !string.Equals(k, packKey, StringComparison.Ordinal)));
            return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivateUnresolvedCollision,
                $"content key '{unresolved.ContentKey}' is also shipped by installed pack(s) "
                    + $"[{others}] and no owning pack has been chosen; record an owning-pack choice (or "
                    + $"declare a dependency) before activating '{packKey}'.", decision);
        }

        try
        {
            var candidate = new PackProjectionAuthority(
                decision, packKey, version, tenant, new ActorId(actingPrincipal), now);
            ProjectionStore().ActivateAndRecordProjectionAdmission(
                tenant, packKey, version, Admission(candidate));
            projectionAuthority = candidate;
        }
        catch (PackTransitionStateException)
        {
            return AuditActivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.ActivateNotInstalled, null, decision);
        }

        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Activated, packKey, version, now, null, null, "pack.install.activated",
            ActingPrincipal: actingPrincipal), decision);

        return new PackActivationOutcome(true, packKey, version, null, Decision: decision);
    }

    /// <inheritdoc />
    public PackActivationOutcome Activate(PackInstallContext context, string packKey, string version)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = ActivateCore(
            context.Tenant,
            packKey,
            version,
            context.Now,
            context.Principal,
            context.OwnershipResolutions,
            out var authority);
        return authority is null ? outcome : Project(outcome, authority);
    }

    /// <inheritdoc />
    private PackDeactivationOutcome DeactivateCore(
        TenantId tenant, string packKey, string version, DateTimeOffset now, string? actingPrincipal,
        out PackProjectionAuthority? projectionAuthority)
    {
        projectionAuthority = null;
        if (string.IsNullOrWhiteSpace(packKey))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.RefusedBlankPackKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.RefusedBlankVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        // Same posture as Activate: the reverse pointer flip is a consequential mutation — refuse +
        // audit an anonymous caller.
        if (string.IsNullOrWhiteSpace(actingPrincipal))
        {
            AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.DeactivateRefusedNoPrincipal);
            ArgumentException.ThrowIfNullOrWhiteSpace(actingPrincipal);
        }

        var decision = AuthorizeOrAudit(tenant, actingPrincipal, now, packKey, version);

        var active = _store.GetActive(tenant, packKey);
        if (active is null || !string.Equals(active.Version, version, StringComparison.Ordinal))
        {
            return AuditDeactivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.DeactivateNotActive, decision);
        }

        try
        {
            var candidate = new PackProjectionAuthority(
                decision, packKey, version, tenant, new ActorId(actingPrincipal), now);
            ProjectionStore().DeactivateAndRecordProjectionAdmission(
                tenant, packKey, version, Admission(candidate));
            projectionAuthority = candidate;
        }
        catch (PackTransitionStateException)
        {
            return AuditDeactivationRefusal(tenant, packKey, version, now, actingPrincipal,
                PackInstallCodes.DeactivateNotActive, decision);
        }

        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Deactivated, packKey, version, now, null, null,
            "pack.install.deactivated",
            ActingPrincipal: actingPrincipal), decision);

        return new PackDeactivationOutcome(true, packKey, version, null, Decision: decision);
    }

    /// <inheritdoc />
    public PackDeactivationOutcome Deactivate(PackInstallContext context, string packKey, string version)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = DeactivateCore(
            context.Tenant, packKey, version, context.Now, context.Principal, out var authority);
        return authority is null ? outcome : Project(outcome, authority);
    }

    /// <inheritdoc />
    public PackNarrowingOutcome Narrow(
        PackInstallContext context, string packKey, string contentKey, JsonNode overlayPatch,
        AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(overlayPatch);
        ArgumentNullException.ThrowIfNull(decision);
        var tenant = context.Tenant;
        var now = context.Now;
        var principal = context.Principal;
        if (string.IsNullOrWhiteSpace(packKey))
        {
            AuditPreDecisionRefusal(tenant, packKey, null, now, principal, PackInstallCodes.RefusedBlankPackKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        }
        if (string.IsNullOrWhiteSpace(contentKey))
        {
            AuditPreDecisionRefusal(
                tenant, packKey, null, now, principal, PackInstallCodes.NarrowUnknownContentKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        }

        // Same posture as Activate/Deactivate: a narrowing changes what the tenant's live definitions
        // say, so the domain layer itself requires the server-derived acting principal.
        if (string.IsNullOrWhiteSpace(principal))
        {
            AuditPreDecisionRefusal(
                tenant, packKey, null, now, principal, PackInstallCodes.NarrowRefusedNoPrincipal);
            ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        }

        // The caller's guard owns the decision; validate its evidence before reading or mutating state.
        decision.RequireAllowedReaction(
            AuthorizationOperation.Parse(Permission.PackagesOperate), tenant, "pack", packKey);
        if (decision.Request.Principal != new ActorId(principal) || decision.Request.At != now)
            throw new ArgumentException("The narrowing context must match the admitting decision.", nameof(decision));
        var active = _store.GetActive(tenant, packKey);

        if (active is null)
        {
            return AuditNarrowingRefusal(
                tenant, packKey, contentKey, now, principal, PackInstallCodes.NarrowNotActive, null, decision);
        }

        var item = active.SeedItems.FirstOrDefault(seed => string.Equals(seed.Key, contentKey, StringComparison.Ordinal));
        if (item is null)
        {
            return AuditNarrowingRefusal(
                tenant, packKey, contentKey, now, principal,
                PackInstallCodes.NarrowUnknownContentKey, null, decision);
        }

        if (!PackTenantNarrowing.IsNarrowing(item.ParseContent(), overlayPatch, out var wideningPath))
        {
            return AuditNarrowingRefusal(
                tenant, packKey, contentKey, now, principal,
                PackTenantNarrowing.WideningRefusedCode, wideningPath, decision);
        }

        _mutations.SaveOverride(tenant, packKey, new PackTenantOverride(contentKey, overlayPatch.DeepClone()));
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Narrowed, packKey, active.Version, now, null, null,
            $"pack.install.narrowed:{contentKey}",
            ActingPrincipal: principal), decision);
        return new PackNarrowingOutcome(true, packKey, contentKey, Decision: decision);
    }

    private PackNarrowingOutcome AuditNarrowingRefusal(
        TenantId tenant, string packKey, string contentKey, DateTimeOffset now, string? principal,
        string code, string? wideningPath, AuthorizationDecision decision)
    {
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Refused, packKey, string.Empty, now, null, null,
            $"{code}:{contentKey}",
            ActingPrincipal: principal), decision);
        return new PackNarrowingOutcome(false, packKey, contentKey, code, wideningPath, decision);
    }

    void IPackProjectionReconciler.AttachProjector(IPackProjectionDispatcher projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        if (_projector is not null && !ReferenceEquals(_projector, projector))
            throw new InvalidOperationException("The pack installer projector is already attached.");
        _projector = projector;
    }

    void IPackProjectionReconciler.ReconcilePending(CancellationToken cancellationToken)
    {
        var projector = _projector
            ?? throw new InvalidOperationException("Pack projection is not composed.");
        foreach (var tenantAdmissions in ProjectionStore().ListIncompleteProjectionAdmissions()
                     .GroupBy(admission => admission.Tenant)
                     .OrderBy(group => group.Key.Value, StringComparer.Ordinal))
        {
            foreach (var admission in tenantAdmissions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authority = PackProjectionAuthority.FromAdmission(admission);
                try
                {
                    var result = projector.Project(authority, cancellationToken);
                    if (Admitted(result))
                    {
                        ProjectionStore().MarkProjectionCompleted(admission.AdmissionId);
                    }
                }
                finally
                {
                    authority.Retire();
                }
            }
        }
    }

    private PackActivationOutcome Project(
        PackActivationOutcome outcome,
        PackProjectionAuthority authority)
    {
        return ProjectAndRetire(
            outcome,
            authority,
            static (current, result) => current with { Projected = true, ProjectionResult = result },
            static (current, ex) => current with { Detail = ex.Message });
    }

    private PackDeactivationOutcome Project(
        PackDeactivationOutcome outcome,
        PackProjectionAuthority authority)
    {
        return ProjectAndRetire(
            outcome,
            authority,
            static (current, result) => current with { Projected = true, ProjectionResult = result },
            static (current, ex) => current with { ProjectionResult = ex });
    }

    private TOutcome ProjectAndRetire<TOutcome>(
        TOutcome outcome,
        PackProjectionAuthority authority,
        Func<TOutcome, object?, TOutcome> succeeded,
        Func<TOutcome, Exception, TOutcome> failed)
    {
        try
        {
            if (_projector is null)
                return outcome;
            try
            {
                var result = _projector.Project(authority);
                if (Admitted(result))
                {
                    ProjectionStore().MarkProjectionCompleted(authority.Nonce);
                }

                return succeeded(outcome, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return failed(outcome, ex);
            }
        }
        finally
        {
            authority.Retire();
        }
    }

    /// <summary>
    /// A pass that refused an item leaves its admission INCOMPLETE, so the next boot's
    /// <see cref="IPackProjectionReconciler.ReconcilePending"/> re-runs it. Only a pass that refused
    /// nothing has actually admitted the pack's definitions.
    /// </summary>
    // ponytail: retries every boot while the refusal stands; bound it with an attempt counter on
    // PackProjectionAdmission if a permanently invalid pack proves noisy.
    private static bool Admitted(object? result) =>
        result is not IPackProjectionRefusalReport report || !report.ProjectionRefused;

    private IPackProjectionAdmissionStore ProjectionStore() => _projectionStore
        ?? throw new InvalidOperationException("The pack install store does not support atomic projection admissions.");

    private static PackProjectionAdmission Admission(PackProjectionAuthority authority) => new(
        authority.Nonce,
        authority.PackId,
        authority.PackVersion,
        authority.Tenant,
        authority.Principal,
        authority.ActivationInstant,
        authority.DerivationIds,
        Projected: false);

    private AuthorizationDecision AuthorizeOrAudit(
        TenantId tenant,
        string principal,
        DateTimeOffset at,
        string packKey,
        string version)
    {
        var scope = ScopeExpression.Parse($"/records/{packKey}");
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            new ActorId(principal),
            tenant,
            new AuthorizationTarget("pack", packKey, scope),
            at);
        var decision = _gate.DecideAsync(request).AsTask().GetAwaiter().GetResult();
        try
        {
            decision.RequireAllowed();
        }
        catch (AuthorizationDeniedException)
        {
            AuditPreDecisionRefusal(
                tenant, packKey, version, at, principal, PackInstallCodes.RefusedAuthorizationDenied);
            throw;
        }
        return decision;
    }

    // ── plan computation (shared by Preview + Install; no store mutation) ─────────

    private InstallPlan BuildPlan(
        ReadOnlySpan<byte> packBytes,
        PackInstallContext context,
        AuthorizationDecision? decision = null,
        ClaimedPackCoordinates? claimedCoordinates = null)
    {
        var verify = _verifier.Verify(packBytes, context.TrustStore);
        var claimed = claimedCoordinates ?? ReadClaimedCoordinates(packBytes);

        // (S-7) verify-before-effect: only a Verified verdict exposes the manifest/content.
        if (verify.Verdict != PackVerdict.Verified || verify.Manifest is null || verify.Contents is null)
        {
            var failedVerificationRevocationStale =
                context.Revocation.IsStale(context.Now, context.RevocationMaxAge);
            return HardRefusal(claimed.PackKey, claimed.Version,
                new[] { PackInstallCodes.RefusedNotVerified }, failedVerificationRevocationStale);
        }

        var manifest = verify.Manifest;
        var contents = verify.Contents;
        if (decision is not null)
        {
            // Bind the ONE admitting decision to the now-verified identity before any platform or
            // installed-state collaborator can observe the request. A verifier that returns a
            // different manifest than the bytes named at authorization fails closed here.
            new PackProjectionAuthority(
                decision,
                manifest.Key,
                manifest.Version,
                context.Tenant,
                new ActorId(context.Principal!),
                context.Now).RequireValid();
        }
        var revocationStale = context.Revocation.IsStale(context.Now, context.RevocationMaxAge);
        var signerKeyId = verify.SignerKeyId;
        var epoch = verify.Epoch;
        var scope = verify.VouchingScope;
        var signerB64 = signerKeyId?.ToBase64Url();

        // (S-11) channel revocation — a revoked {key, epoch} is refused before any effect.
        if (signerKeyId is { } sk && epoch is { } ep && context.Revocation.IsRevoked(sk, ep))
        {
            return HardRefusal(manifest.Key, manifest.Version, new[] { PackInstallCodes.RefusedRevoked }, revocationStale,
                signerB64, epoch, scope);
        }

        // (S-13) scope-vs-content — structural fail-closed extension point (no v1 first-party scope refuses).
        if (scope is { } vouch && !PackScopePolicy.MaySeed(vouch))
        {
            return HardRefusal(manifest.Key, manifest.Version, new[] { PackInstallCodes.RefusedScope }, revocationStale,
                signerB64, epoch, scope);
        }

        // A verified declaration is not enough: content must have a live projection path in this build.
        // NavWorkspaceConfig is intentionally absent because PackNavigationRoutes projects it directly
        // from active immutable seeds on read. These three kinds currently have no consumer at all.
        if (contents.Any(content => content.Kind == PackContentKind.StandardsCatalog))
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                new[] { PackInstallCodes.RefusedUnsupportedStandardsCatalog },
                revocationStale,
                signerB64,
                epoch,
                scope);
        }

        if (contents.Any(content => content.Kind == PackContentKind.CascadeDefaults))
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                new[] { PackInstallCodes.RefusedUnsupportedCascadeDefaults },
                revocationStale,
                signerB64,
                epoch,
                scope);
        }

        if (contents.Any(content => content.Kind == PackContentKind.TerminologyOverride))
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                new[] { PackInstallCodes.RefusedUnsupportedTerminologyOverride },
                revocationStale,
                signerB64,
                epoch,
                scope);
        }

        var unmetRequirements = PackPlatformRequirementCheck.FindUnmet(manifest, contents, _platform);
        if (unmetRequirements.FirstOrDefault()?.Failure == PackPlatformRequirementFailure.MissingCapability)
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                new[] { PackInstallCodes.RefusedMissingPlatformCapability },
                revocationStale,
                signerB64,
                epoch,
                scope,
                unmetRequirements);
        }

        if (unmetRequirements.Count > 0)
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                new[] { PackInstallCodes.RefusedPlatformVersionFloor },
                revocationStale,
                signerB64,
                epoch,
                scope,
                unmetRequirements);
        }

        // ONE install-state snapshot serves all four installed-state checks below (prior version,
        // cross-pack collisions, content references, dependency presence).
        var installed = _store.ListInstalled(context.Tenant);
        var prior = _store.GetActive(context.Tenant, manifest.Key) ?? LatestInstalled(installed, manifest.Key);
        var watermark = _store.GetWatermark(context.Tenant, manifest.Key);
        var isUpgrade = watermark is not null;

        // (S-8) monotonic version + floor watermark hits. Floor extraction is the SINGLE SOURCE shared with
        // the author-side guard (PackSafetyFloors.ExtractFloors) — council A-3, no reimplementation.
        var newFloors = PackSafetyFloors.ExtractFloors(contents);
        var watermarkHits = new List<PackWatermarkHit>();
        if (watermark is not null)
        {
            if (PackVersion.IsDowngrade(watermark.Version, manifest.Version))
            {
                watermarkHits.Add(new PackWatermarkHit(
                    PackWatermarkHitKind.VersionDowngrade,
                    $"version {manifest.Version} is below the installed watermark {watermark.Version}"));
            }

            var weakened = PackSafetyFloors.Weakened(watermark.Floors, newFloors);
            if (weakened.Count > 0)
            {
                watermarkHits.Add(new PackWatermarkHit(
                    PackWatermarkHitKind.FloorWeakened,
                    $"safety floor(s) weakened below the watermark: {string.Join(", ", weakened)}"));
            }
        }

        // (S-10) total re-attach of prior overrides onto the new seed.
        var priorSeeds = prior?.SeedItems ?? (IReadOnlyList<PackSeedItem>)Array.Empty<PackSeedItem>();
        var priorOverrides = _store.GetOverrides(context.Tenant, manifest.Key);
        var reattach = PackReattachPlanner.Plan(priorSeeds, contents, priorOverrides, manifest.RenamedFrom);

        // (S-9) ADR 0143 admission over the COMPOSED post-install cascade (seed ⊕ re-attached overrides).
        var composed = BuildComposed(manifest.Key, contents, reattach.Reattached);
        var admission = _admission.Admit(composed, context.Tenant);
        if (admission.IsAdmissible)
        {
            var navigationCollision = FindNavigationCompositionRefusal(installed, manifest.Key, composed);
            if (navigationCollision is not null)
            {
                admission = new PackAdmissionResult(
                [
                    new PackAdmissionRefusal(
                        composed.First(item => item.Kind == PackContentKind.NavWorkspaceConfig).Key,
                        navigationCollision.Code,
                        navigationCollision.Message),
                ]);
            }
        }

        // (§6.3 / G2) Content-grain cross-app reference validation — a declared reference into another app
        // must resolve to an INSTALLED contributable, else install refuses fail-closed naming the missing
        // app. This is the content-grain counterpart to a pack-level dependency, surfaced structurally so the
        // client localizes "requires X — not installed" (never an English literal).
        var unmetReferences = DetectUnmetContentReferences(installed, manifest, contents);

        // (Ticket 152) Pack-level declared-dependency PRESENCE check: every manifest.Dependencies entry
        // must resolve to an installed pack at the pinned-or-newer version. The single-level-only rule
        // (A9, PackValidator) is untouched — this checks the one level v1 declares actually EXISTS.
        var unmetDependencies = DetectUnmetDependencies(installed, manifest);

        // Verdict.
        PackInstallVerdict verdict;
        IReadOnlyList<string> refusalCodes;
        if (!admission.IsAdmissible)
        {
            verdict = PackInstallVerdict.Refused;
            refusalCodes = new[] { PackInstallCodes.RefusedAdmission };
        }
        else if (unmetReferences.Count > 0)
        {
            // A missing dependency is a HARD refusal (not S-8 break-glass): you cannot wave through content
            // that binds a type no installed app provides — install the required app first.
            verdict = PackInstallVerdict.Refused;
            refusalCodes = new[] { PackInstallCodes.RefusedUnmetContentReference };
        }
        else if (unmetDependencies.Count > 0)
        {
            // Same posture as unmet content references: a declared dependency that is not installed is a
            // HARD fail-closed refusal naming the missing dependency — never break-glass-overridable. A
            // MALFORMED pin carries its own distinct code (it was refused unread, not compared-and-missed).
            verdict = PackInstallVerdict.Refused;
            var dependencyCodes = new List<string>(capacity: 2);
            if (unmetDependencies.Any(d => !d.MalformedPin))
            {
                dependencyCodes.Add(PackInstallCodes.RefusedUnmetDependency);
            }
            if (unmetDependencies.Any(d => d.MalformedPin))
            {
                dependencyCodes.Add(PackInstallCodes.RefusedMalformedDependencyPin);
            }
            refusalCodes = dependencyCodes;
        }
        else if (watermarkHits.Count > 0)
        {
            verdict = PackInstallVerdict.RequiresBreakGlass;
            refusalCodes = watermarkHits
                .Select(h => h.Kind == PackWatermarkHitKind.VersionDowngrade
                    ? PackInstallCodes.RefusedDowngrade
                    : PackInstallCodes.RefusedFloorWeakened)
                .Distinct()
                .ToList();
        }
        else
        {
            verdict = isUpgrade ? PackInstallVerdict.WouldUpgrade : PackInstallVerdict.WouldInstall;
            refusalCodes = Array.Empty<string>();
        }

        // (ADR 0129 D4/D5 — F4) Cross-pack same-key collisions: does this candidate ship a content key an
        // already-installed OTHER pack also ships? SURFACED here (never silently first-wins-merged, S-2);
        // install stays additive, and an UNRESOLVED collision is enforced fail-closed at ACTIVATE.
        var crossPackCollisions = DetectCandidateCollisions(context.Tenant, installed, manifest, contents);

        var preview = new PackInstallPreview(
            verdict, manifest.Key, manifest.Version, signerB64, epoch, scope, isUpgrade, prior?.Version,
            NewSeedKeys: contents.Select(c => c.Key).ToList(),
            Conflicts: reattach.Conflicts,
            WatermarkHits: watermarkHits,
            AdmissionRefusals: admission.Refusals,
            RevocationStale: revocationStale,
            RefusalCodes: refusalCodes,
            CrossPackCollisions: crossPackCollisions,
            UnmetContentReferences: unmetReferences,
            UnmetPlatformRequirements: Array.Empty<PackUnmetPlatformRequirement>())
        {
            UnmetDependencies = unmetDependencies,
        };

        // (S-4) Anti-laundering-by-OMISSION: the installed seed's own floor set must never DROP a floor the
        // established watermark holds just because this version omitted it. Clamp the seed's floors so an
        // omitted key inherits the watermarked max (a declared floor — raised, or a break-glass-authorized
        // lowering — still wins). Without this, a vNext that silently drops a `safetyFloors` entry installs
        // clean yet the floor vanishes from the seed layer — the S-4 "must not launder a floor" outcome via
        // omission. The watermark itself already survives omission (elementwise Max); this closes the SEED.
        var effectiveFloors = watermark is null
            ? newFloors
            : PackSafetyFloors.PreserveOmitted(newFloors, watermark.Floors);

        var newPack = BuildInstalledPack(manifest, contents, effectiveFloors, signerKeyId!.Value, epoch!.Value, scope!.Value, context.Now);
        var newWatermark = AdvanceWatermark(manifest.Key, watermark, manifest.Version, newFloors);

        return new InstallPlan(
            preview, signerKeyId, epoch, reattach, newPack, newWatermark,
            isUpgrade ? PackInstallAuditAction.Upgraded : PackInstallAuditAction.Installed);
    }

    private static ClaimedPackCoordinates ReadClaimedCoordinates(ReadOnlySpan<byte> packBytes)
    {
        var coordinates = PackManifestCoordinateReader.TryRead(packBytes);
        return coordinates is null
            ? ClaimedPackCoordinates.Unverified
            : new ClaimedPackCoordinates(coordinates.Value.PackKey, coordinates.Value.Version);
    }

    private InstallPlan HardRefusal(
        string packKey, string version, IReadOnlyList<string> codes, bool revocationStale,
        string? signerB64 = null,
        long? epoch = null,
        Harborline.Api.Foundation.Packs.Trust.TrustScope? scope = null,
        IReadOnlyList<PackUnmetPlatformRequirement>? unmetPlatformRequirements = null)
    {
        var preview = new PackInstallPreview(
            PackInstallVerdict.Refused, packKey, version, signerB64, epoch, scope,
            IsUpgrade: false, PriorVersion: null,
            NewSeedKeys: Array.Empty<string>(),
            Conflicts: Array.Empty<PackReattachConflict>(),
            WatermarkHits: Array.Empty<PackWatermarkHit>(),
            AdmissionRefusals: Array.Empty<PackAdmissionRefusal>(),
            RevocationStale: revocationStale,
            RefusalCodes: codes,
            CrossPackCollisions: Array.Empty<PackCrossPackCollision>(),
            UnmetContentReferences: Array.Empty<PackUnmetContentReference>(),
            UnmetPlatformRequirements: unmetPlatformRequirements ?? Array.Empty<PackUnmetPlatformRequirement>());
        return new InstallPlan(preview, null, epoch, null, null, null, PackInstallAuditAction.Refused);
    }

    private void AuditRefused(
        PackInstallContext context,
        PackInstallPreview preview,
        Harborline.Api.Foundation.Crypto.PrincipalId? signerKeyId,
        AuthorizationDecision decision)
        => _audit.AppendAuthorized(new PackInstallAuditEntry(
            context.Tenant, PackInstallAuditAction.Refused, preview.PackKey, preview.Version,
            context.Now, signerKeyId, preview.Epoch, Detail: string.Join(",", preview.RefusalCodes),
            ActingPrincipal: context.Principal), decision);

    private void AuditPreDecisionRefusal(
        TenantId tenant,
        string? packKey,
        string? version,
        DateTimeOffset now,
        string? actingPrincipal,
        string detail)
        => _audit.Append(new PackInstallAuditEntry(
            tenant,
            PackInstallAuditAction.Refused,
            packKey ?? string.Empty,
            version ?? string.Empty,
            now,
            null,
            null,
            Detail: detail,
            ActingPrincipal: actingPrincipal,
            PreDecision: true));

    private PackActivationOutcome AuditActivationRefusal(
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string actingPrincipal,
        string error,
        string? detail,
        AuthorizationDecision decision)
    {
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Refused, packKey, version, now, null, null,
            Detail: detail is null ? error : $"{error}: {detail}", ActingPrincipal: actingPrincipal), decision);
        return new PackActivationOutcome(false, packKey, version, error, detail, Decision: decision);
    }

    private PackDeactivationOutcome AuditDeactivationRefusal(
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string actingPrincipal,
        string error,
        AuthorizationDecision decision)
    {
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Refused, packKey, version, now, null, null,
            Detail: error, ActingPrincipal: actingPrincipal), decision);
        return new PackDeactivationOutcome(false, packKey, version, error, Decision: decision);
    }

    private static InstalledPack? LatestInstalled(IReadOnlyList<InstalledPack> installed, string packKey)
        => installed
            .Where(p => p.PackKey == packKey)
            .OrderBy(p => p.Version, VersionComparer)
            .LastOrDefault();

    /// <summary>
    /// Detects the cross-pack same-key collisions a CANDIDATE (not-yet-installed) pack would create with
    /// the already-installed packs — the install-preview surface. Filters to collisions the candidate is
    /// actually a claimant of. Runs the shared <see cref="PackCompositionConflicts.Detect"/> over the
    /// existing installed claims (excluding any prior version of the candidate's own key — that is an
    /// UPGRADE, handled by S-10 re-attach, not a cross-pack collision) PLUS the candidate's own claim.
    /// </summary>
    private IReadOnlyList<PackCrossPackCollision> DetectCandidateCollisions(
        TenantId tenant, IReadOnlyList<InstalledPack> installed, PackManifest manifest,
        IReadOnlyList<PackContentItem> contents)
    {
        // Existing installed claims EXCLUDING the candidate's own key (a same-key clash with a prior version
        // of THIS pack is an upgrade — S-10 re-attach — not a cross-pack collision) plus the candidate.
        var claims = PackCompositionConflicts.ClaimsFromInstalled(installed, excludePackKey: manifest.Key);
        claims.Add(new PackKeyClaim(
            manifest.Key,
            contents.Select(c => new PackClaimedContent(c.Key, c.Kind)).ToList(),
            manifest.Dependencies.Select(d => d.Key).ToList()));
        return PackCompositionConflicts.Detect(claims, _store.GetKeyOwnership(tenant))
            .Where(c => c.ClaimingPackKeys.Contains(manifest.Key, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Validates the candidate's declared content-grain cross-app references (design note §6.3, slice G2):
    /// each reference must resolve to an INSTALLED contributable — an installed pack whose key is
    /// <c>toPackKey</c> that carries <c>toContentKey</c>. A self-reference (into the pack being installed) is
    /// satisfied by the candidate's own contents. Resolution is INSTALLED-based (any lifecycle), mirroring the
    /// cross-pack collision engine (cerebrum 2026-07-07): a referenced app's types exist in its seed layer
    /// once installed; activation is the tenant's separate step, so requiring the dependency to be already
    /// ACTIVE would create needless draft-ordering friction. Returns the UNMET references (empty ⇒ all
    /// resolve), which the caller renders as a fail-closed refusal naming the missing app(s).
    /// </summary>
    private static IReadOnlyList<PackUnmetContentReference> DetectUnmetContentReferences(
        IReadOnlyList<InstalledPack> installed, PackManifest manifest, IReadOnlyList<PackContentItem> contents)
    {
        if (manifest.ContentReferences is not { Count: > 0 } references)
        {
            return Array.Empty<PackUnmetContentReference>(); // pre-G2 / dependency-free pack ⇒ nothing to check.
        }

        var ownKeys = new HashSet<string>(contents.Select(c => c.Key), StringComparer.Ordinal);
        var provided = new HashSet<(string PackKey, string ContentKey)>();
        foreach (var pack in installed)
        {
            foreach (var item in pack.SeedItems)
            {
                provided.Add((pack.PackKey, item.Key));
            }
        }

        var unmet = new List<PackUnmetContentReference>();
        foreach (var reference in references)
        {
            var satisfiedBySelf =
                string.Equals(reference.ToPackKey, manifest.Key, StringComparison.Ordinal)
                && ownKeys.Contains(reference.ToContentKey);
            var satisfiedByInstalled = provided.Contains((reference.ToPackKey, reference.ToContentKey));

            if (!satisfiedBySelf && !satisfiedByInstalled)
            {
                unmet.Add(new PackUnmetContentReference(
                    reference.FromContentKey, reference.ToPackKey, reference.ToContentKey, reference.Relation));
            }
        }

        unmet.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.FromContentKey, b.FromContentKey);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ToPackKey, b.ToPackKey);
            if (c != 0) return c;
            return string.CompareOrdinal(a.ToContentKey, b.ToContentKey);
        });
        return unmet;
    }

    /// <summary>
    /// Ticket 152 — resolves each manifest-DECLARED dependency (single-level, A9) against the installed
    /// packs: unmet when NO version of the key is installed, or the best installed version is below the
    /// pin (per <see cref="PackVersion.Compare"/>; a NEWER installed version satisfies the pin — S-8
    /// keeps installs monotonic, so an exact-pin rule would break every dependent after a dependency
    /// upgrade). INSTALLED-based (any lifecycle), mirroring <see cref="DetectUnmetContentReferences"/>.
    /// A self-dependency is satisfied by the pack being installed. Returns the UNMET entries sorted by
    /// key (empty ⇒ all resolve).
    /// </summary>
    private static IReadOnlyList<PackUnmetDependency> DetectUnmetDependencies(
        IReadOnlyList<InstalledPack> installed, PackManifest manifest)
    {
        if (manifest.Dependencies is not { Count: > 0 } dependencies)
        {
            return Array.Empty<PackUnmetDependency>();
        }

        var unmet = new List<PackUnmetDependency>();
        foreach (var dependency in dependencies)
        {
            if (string.Equals(dependency.Key, manifest.Key, StringComparison.Ordinal))
            {
                continue; // self-reference: satisfied by this very install.
            }

            var best = LatestInstalled(installed, dependency.Key);

            // Fail-closed on the PIN direction (ADR 0038): PackVersion.Compare degrades an unparseable
            // segment to 0 — the right posture for a candidate (lowest possible), the WRONG one for a
            // pin (a pin of 0 is trivially satisfied by anything). Refuse the malformed pin unread.
            if (!PackVersion.IsWellFormed(dependency.Version))
            {
                unmet.Add(new PackUnmetDependency(
                    dependency.Key, dependency.Version, best?.Version, MalformedPin: true));
                continue;
            }

            if (best is null)
            {
                unmet.Add(new PackUnmetDependency(dependency.Key, dependency.Version, null));
            }
            else if (PackVersion.Compare(best.Version, dependency.Version) < 0)
            {
                unmet.Add(new PackUnmetDependency(dependency.Key, dependency.Version, best.Version));
            }
        }

        unmet.Sort((a, b) => string.CompareOrdinal(a.DependencyKey, b.DependencyKey));
        return unmet;
    }

    private static PackNavigationRefusal? FindNavigationCompositionRefusal(
        IReadOnlyList<InstalledPack> installed,
        string candidatePackKey,
        IReadOnlyList<PackComposedItem> candidate)
    {
        var candidateNavigation = candidate
            .Where(item => item.Kind == PackContentKind.NavWorkspaceConfig)
            .ToArray();
        if (candidateNavigation.Length == 0)
            return null;

        var declarations = new List<PackNavigationDeclaration>();
        foreach (var packGroup in installed
                     .Where(pack => !string.Equals(pack.PackKey, candidatePackKey, StringComparison.Ordinal))
                     .GroupBy(pack => pack.PackKey, StringComparer.Ordinal))
        {
            var pack = packGroup.FirstOrDefault(item => item.Lifecycle == PackLifecycleState.Active)
                       ?? packGroup.OrderBy(item => item.Version, VersionComparer).Last();
            foreach (var item in pack.SeedItems.Where(item => item.Kind == PackContentKind.NavWorkspaceConfig))
            {
                if (!PackNavigationDeclarationParser.DeclaresNavigation(item.CanonicalJson))
                    continue;
                var parsed = PackNavigationDeclarationParser.Parse(item.CanonicalJson);
                if (!parsed.Succeeded)
                    return parsed.Refusal;
                declarations.Add(parsed.Declaration!);
            }
        }
        foreach (var item in candidateNavigation)
        {
            if (!PackNavigationDeclarationParser.DeclaresNavigation(item.CanonicalJson))
                continue;
            var parsed = PackNavigationDeclarationParser.Parse(item.CanonicalJson);
            if (!parsed.Succeeded)
                return parsed.Refusal;
            declarations.Add(parsed.Declaration!);
        }
        return PackNavigationDeclarationParser.FindCompositionRefusal(declarations);
    }

    private static IReadOnlyList<PackComposedItem> BuildComposed(
        string packageKey,
        IReadOnlyList<PackContentItem> contents,
        IReadOnlyList<PackTenantOverride> reattached)
    {
        var overrideByKey = reattached.ToDictionary(o => o.ContentKey, o => o.OverlayPatch, StringComparer.Ordinal);
        var composed = new List<PackComposedItem>(contents.Count);
        foreach (var item in contents)
        {
            var baseNode = JsonNode.Parse(Encoding.UTF8.GetString(item.CanonicalBytes.Span));
            var effective = overrideByKey.TryGetValue(item.Key, out var patch)
                ? TemplateMerger.ApplyMergePatch(baseNode, patch)
                : baseNode;
            composed.Add(new PackComposedItem(
                packageKey, item.Key, item.Kind, item.Version, effective?.ToJsonString() ?? "null"));
        }

        return composed;
    }

    private static InstalledPack BuildInstalledPack(
        PackManifest manifest,
        IReadOnlyList<PackContentItem> contents,
        IReadOnlyDictionary<string, int> floors,
        Harborline.Api.Foundation.Crypto.PrincipalId signerKeyId,
        long epoch,
        Harborline.Api.Foundation.Packs.Trust.TrustScope scope,
        DateTimeOffset now)
    {
        var seeds = contents
            .Select(c => new PackSeedItem(
                c.Key, c.Kind, c.Version, Encoding.UTF8.GetString(c.CanonicalBytes.Span), c.ContentAddress))
            .ToList();

        return new InstalledPack(
            manifest.Key, manifest.Version, manifest.ScopeTier, PackLifecycleState.Draft,
            seeds, floors, now, signerKeyId, epoch, scope,
            // Persist the signed manifest's dependency edges + provider slot + content-grain reference edges
            // so cross-pack collision resolution (D5 chain precedence), activate-exclusivity, and the feature
            // graph's class-3 edge (G2) all work off durable install state — never a re-read of the pack file.
            manifest.Dependencies,
            manifest.ProviderSlot,
            manifest.ContentReferences,
            manifest.CapabilityRequirements);
    }

    private static PackInstallWatermark AdvanceWatermark(
        string packKey, PackInstallWatermark? current, string newVersion, IReadOnlyDictionary<string, int> newFloors)
    {
        if (current is null)
        {
            return new PackInstallWatermark(packKey, newVersion, newFloors);
        }

        // Monotonic: the watermark version is the HIGHER of the two; floors are elementwise-max. A
        // break-glass downgrade installs the lower seed but NEVER lowers the watermark (S-8 monotonic).
        var highestVersion = PackVersion.Compare(newVersion, current.Version) > 0 ? newVersion : current.Version;
        return new PackInstallWatermark(packKey, highestVersion, PackSafetyFloors.Max(current.Floors, newFloors));
    }

    /// <summary>The internal plan shared by Preview + Install — the built preview plus everything the
    /// commit needs (never mutates a store).</summary>
    private sealed record InstallPlan(
        PackInstallPreview Preview,
        Harborline.Api.Foundation.Crypto.PrincipalId? SignerKeyId,
        long? Epoch,
        PackReattachPlan? Reattach,
        InstalledPack? NewInstalledPack,
        PackInstallWatermark? NewWatermark,
        PackInstallAuditAction SuccessAction);

    private sealed record ClaimedPackCoordinates(string PackKey, string Version)
    {
        internal static ClaimedPackCoordinates Unverified { get; } =
            new("(unverified)", "(unverified)");
    }
}
