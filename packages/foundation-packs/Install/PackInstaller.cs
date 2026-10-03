using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Catalog.Templates;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Kernel.Core;

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
    private readonly IWritePipelineObserver? _pipelineObserver;
    private IPackProjectionDispatcher? _projector;

    /// <summary>Constructs the installer over the verifier + install store + admission port + audit sink.</summary>
    public PackInstaller(
        IPackVerifier verifier,
        IPackInstallStore store,
        IPackContentAdmission admission,
        IPackInstallAudit audit,
        AuthorizationGate gate,
        IPackPlatformCompatibility? platform = null,
        IWritePipelineObserver? pipelineObserver = null)
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
        _pipelineObserver = pipelineObserver;
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
        IPackPlatformCompatibility? platform = null,
        IWritePipelineObserver? pipelineObserver = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _platform = platform ?? PackPlatformCompatibility.Empty;
        _pipelineObserver = pipelineObserver;
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
        IPackPlatformCompatibility? platform = null,
        IWritePipelineObserver? pipelineObserver = null)
        : this(verifier, store, mutations, projectionStore, admission, audit, gate, platform, pipelineObserver)
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
        IPackPlatformCompatibility? platform = null,
        IWritePipelineObserver? pipelineObserver = null)
        : this(verifier, store, admission, audit, gate, platform, pipelineObserver)
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
    public PackInstallPreview Check(ReadOnlySpan<byte> packBytes, PackInstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return BuildPlan(packBytes, context, collectRefusals: true).Preview;
    }

    /// <inheritdoc />
    public Task<PackInstallOutcome> InstallAsync(
        ReadOnlyMemory<byte> packBytes, PackInstallContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(new Installation(this, packBytes, context), cancellationToken);
    }

    /// <summary>
    /// Pack install as its six ADR 0038 stages (ck-10 S5c). Authorize reads only the claimed coordinates and
    /// keeps the audited pre-decision refusals. Bind reads installed versions, watermark, overrides and tenant ownership. Mutate is
    /// the plan and its <see cref="PackInstallTransaction"/>. Validate runs the hard refusals (verify, revocation,
    /// scope, projector support, admission) and the S-8 watermark rule, which proceeds only under the explicit
    /// break-glass ceremony. Commit is the atomic seed-layer commit. React is the audit, the break-glass entry
    /// first, and the outcome.
    /// </summary>
    private sealed class Installation(PackInstaller installer, ReadOnlyMemory<byte> packBytes, PackInstallContext context)
        : KernelWrite<InstallBound, InstallMutation, InstallSealed, PackInstallOutcome>
    {
        private ClaimedPackCoordinates claimed = null!;
        private AuthorizationDecision decision = null!;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            claimed = ReadClaimedCoordinates(packBytes.Span);
            var principal = context.Principal;
            if (string.IsNullOrWhiteSpace(principal))
            {
                installer.AuditPreDecisionRefusal(
                    context.Tenant, claimed.PackKey, claimed.Version, context.Now, principal,
                    PackInstallCodes.RefusedNoPrincipal);
                ArgumentException.ThrowIfNullOrWhiteSpace(principal, "context.Principal");
            }

            decision = installer.AuthorizeOrAudit(context.Tenant,
                principal, context.Now, claimed.PackKey, claimed.Version, context.CorrelationId);
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<InstallBound?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult<InstallBound?>(new InstallBound(
                installer._store.ListInstalled(context.Tenant),
                claimed.PackKey,
                installer._store.GetWatermark(context.Tenant, claimed.PackKey),
                installer._store.GetOverrides(context.Tenant, claimed.PackKey),
                installer._store.GetKeyOwnership(context.Tenant)));

        protected override ValueTask<InstallMutation> MutateAsync(InstallBound bound, CancellationToken ct)
        {
            var plan = installer.BuildPlan(packBytes.Span, context, decision, claimed, bound: bound);
            // A hard refusal plans no seed layer, so there is no transaction for validate to seal.
            var transaction = plan.NewInstalledPack is null
                ? null
                : new PackInstallTransaction(context.Tenant, plan.NewInstalledPack, plan.NewWatermark!, plan.Reattach!.Reattached)
                {
                    CompareWatermark = true,
                    ExpectedWatermark = bound.Watermark,
                    ExpectedInstalledState = bound.Installed,
                    ExpectedOverrides = bound.Overrides,
                    ExpectedKeyOwnership = bound.KeyOwnership,
                };
            return ValueTask.FromResult(new InstallMutation(plan, transaction));
        }

        protected override ValueTask<InstallSealed> ValidateAsync(
            InstallBound bound, InstallMutation mutation, CancellationToken ct)
        {
            var preview = mutation.Plan.Preview;

            // Hard refusal (verify / revocation / scope / projector support / admission) — never overridable.
            if (preview.Verdict == PackInstallVerdict.Refused)
                throw new Refused(AuditRefusal(mutation.Plan));

            // S-8 watermark refusal — proceeds ONLY under an explicit, audited break-glass ceremony.
            var brokeGlass = false;
            if (preview.Verdict == PackInstallVerdict.RequiresBreakGlass)
            {
                if (context.BreakGlass is null)
                    throw new Refused(AuditRefusal(mutation.Plan));
                brokeGlass = true;
            }

            // T-1048: the audit entries travel into the commit, so a durable store stages them with the seed layer.
            var transaction = mutation.Transaction! with
            {
                Audit = new PackCommitAudit(AuditEntries(mutation.Plan, brokeGlass), decision),
            };
            return ValueTask.FromResult(new InstallSealed(mutation.Plan, transaction, brokeGlass));
        }

        /// <summary>ATOMIC apply (S-7): the seed layer, watermark and re-attached overrides commit all-or-nothing.</summary>
        protected override ValueTask CommitAsync(InstallSealed validated, CancellationToken ct)
        {
            try { installer._mutations.Commit(validated.Transaction); }
            catch (Exception exception) when (exception is PackInstallWatermarkChangedException or PackInstallStateChangedException)
            {
                throw new Refused(AuditRefusal(validated.Plan with
                {
                    Preview = validated.Plan.Preview with
                    {
                        Verdict = PackInstallVerdict.Refused,
                        RefusalCodes = [exception is PackInstallWatermarkChangedException
                            ? PackInstallCodes.RefusedWatermarkChanged : PackInstallCodes.RefusedInstalledStateChanged],
                    },
                }));
            }
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<PackInstallOutcome> ReactAsync(InstallSealed validated, CancellationToken ct)
        {
            var plan = validated.Plan;
            var preview = plan.Preview;
            // The same entry instances the commit carried: a store that staged them is delivering them already.
            foreach (var entry in validated.Transaction.Audit!.Entries)
                installer._audit.AppendAuthorized(entry, decision);

            return ValueTask.FromResult(new PackInstallOutcome(
                true, plan.SuccessAction, preview.PackKey, preview.Version, Array.Empty<string>(), preview,
                validated.BrokeGlass, decision));
        }

        /// <summary>The break-glass ceremony is a DISTINCT, loud entry (S-8), recorded first.</summary>
        private PackInstallAuditEntry[] AuditEntries(InstallPlan plan, bool brokeGlass)
        {
            var preview = plan.Preview;
            var installed = new PackInstallAuditEntry(
                context.Tenant, plan.SuccessAction, preview.PackKey, preview.Version,
                context.Now, plan.SignerKeyId, plan.Epoch,
                Detail: plan.SuccessAction == PackInstallAuditAction.Upgraded ? PackInstallCodes.Upgraded : PackInstallCodes.Installed,
                ActingPrincipal: context.Principal);
            if (!brokeGlass) return [installed];
            return
            [
                new PackInstallAuditEntry(
                    context.Tenant, PackInstallAuditAction.BreakGlassOverride, preview.PackKey, preview.Version,
                    context.Now, plan.SignerKeyId, plan.Epoch,
                    Detail: string.Join(",", preview.RefusalCodes),
                    BreakGlassJustification: context.BreakGlass!.Justification,
                    BreakGlassAuthorizingPrincipal: context.BreakGlass.AuthorizingPrincipal,
                    ActingPrincipal: context.Principal),
                installed,
            ];
        }

        private PackInstallOutcome AuditRefusal(InstallPlan plan)
        {
            installer.AuditRefused(context, plan.Preview, plan.SignerKeyId, decision);
            return new PackInstallOutcome(false, PackInstallAuditAction.Refused, plan.Preview.PackKey, plan.Preview.Version,
                plan.Preview.RefusalCodes, plan.Preview, BrokeGlass: false, decision);
        }
    }

    /// <summary>The installed versions, target watermark and overrides, and tenant ownership choices read by bind.</summary>
    private sealed record InstallBound(
        IReadOnlyList<InstalledPack> Installed, string PackKey, PackInstallWatermark? Watermark,
        IReadOnlyList<PackTenantOverride> Overrides, IReadOnlyDictionary<string, string> KeyOwnership);

    private sealed record InstallMutation(InstallPlan Plan, PackInstallTransaction? Transaction);

    private sealed record InstallSealed(InstallPlan Plan, PackInstallTransaction Transaction, bool BrokeGlass);

    private static PackActivationOutcome? FindClosureRefusal(
        InstalledPack target, IReadOnlyList<InstalledPack> installed, AuthorizationDecision decision)
    {
        // DES-0029 ck-2 S2: the target's whole closure resolves against the Active versions. A key with no
        // Active version is resolved from its latest installed manifest so the refusal says "inactive", not
        // "missing".
        var active = installed
            .Where(pack => pack.Lifecycle == PackLifecycleState.Active && pack.PackKey != target.PackKey)
            .ToDictionary(pack => pack.PackKey, pack => pack.Version, StringComparer.Ordinal);
        active[target.PackKey] = target.Version;
        var manifests = installed
            .Where(pack => pack.PackKey != target.PackKey)
            .GroupBy(pack => pack.PackKey, StringComparer.Ordinal)
            .Select(versions => versions.FirstOrDefault(pack => pack.Lifecycle == PackLifecycleState.Active)
                ?? versions.MaxBy(pack => pack.Version, VersionComparer)!)
            .Append(target)
            .ToDictionary(pack => pack.PackKey, pack => new KernelPackageManifest(pack.PackKey, pack.Version,
                pack.Dependencies
                    .Where(dependency => dependency.Key != pack.PackKey) // a self-reference is this very pack.
                    .Select(dependency => new KernelPackageDependency(dependency.Key, dependency.Version))
                    .ToArray()), StringComparer.Ordinal);
        // D5: the resolver roots every closure at the platform pack, so an inactive platform refuses any other target.
        const string platform = KernelPackageClosure.PlatformPackageKey;
        try
        {
            KernelPackageClosure.Resolve([target.PackKey], manifests.Values, active);
            return null;
        }
        catch (KernelClosureRefusalException refusal)
        {
            var path = $"'{string.Join(" > ", refusal.Path)}'";
            var last = refusal.Path[^1];
            var (error, detail) = refusal.Code switch
            {
                _ when refusal.Path is [platform] => (PackInstallCodes.ActivatePlatformPackRequired,
                    $"the platform pack '{platform}' must be active before '{target.PackKey}' can activate."),
                KernelClosureErrors.DependencyInactive => (PackInstallCodes.ActivateDependencyInactive,
                    $"closure path {path}: '{last}' is installed but not active."),
                KernelClosureErrors.DependencyBelowPin => (PackInstallCodes.ActivateDependencyBelowPin,
                    $"closure path {path}: active {active[last]} is below the pin "
                        + $"{manifests[refusal.Path[^2]].Dependencies.First(dependency => dependency.Key == last).MinimumVersion}."),
                KernelClosureErrors.DependencyMissing => (PackInstallCodes.ActivateDependencyMissing,
                    $"closure path {path}: '{last}' is not installed."),
                KernelClosureErrors.Cycle => (PackInstallCodes.ActivateDependencyCycle, $"closure cycle {path}."),
                _ => (refusal.Code, $"closure path {path}."),
            };
            return new(false, target.PackKey, target.Version, error, detail, Decision: decision);
        }
    }

    private PackActivationOutcome? FindActivationCompositionRefusal(
        TenantId tenant, InstalledPack target, IReadOnlyDictionary<string, string>? ownershipResolutions,
        AuthorizationDecision decision)
    {
        var installed = _store.ListInstalled(tenant);
        if (FindClosureRefusal(target, installed, decision) is { } closureRefusal) return closureRefusal;
        var packKey = target.PackKey;
        var version = target.Version;
        var active = installed
            .Where(pack => pack.Lifecycle == PackLifecycleState.Active)
            .ToList();
        var unmetInterfaces = PackInterfaceRequirementCheck.FindUnmet(target, active);
        var unmetInterface = unmetInterfaces.Count > 0 ? unmetInterfaces[0] : null;
        if (unmetInterface is not null)
        {
            var requirement = $"{unmetInterface.PackKey}@{unmetInterface.InterfaceVersion}";
            return new(false, packKey, version,
                PackInstallCodes.ActivateUnmetInterfaceRequirement,
                $"interface requirement '{requirement}' declared by '{unmetInterface.ContentKey}' is not exposed by any active pack.",
                Decision: decision,
                Refusal: new PackInstallRefusal(PackInstallCodes.ActivateUnmetInterfaceRequirement,
                    ContentPointer(target.SeedItems, unmetInterface.ContentKey)));
        }

        var unexposed = PackInterfaceRequirementCheck.FindUnexposed(target, active);
        if (unexposed is not null)
        {
            return new(false, packKey, version,
                PackInstallCodes.ActivateUnexposedDefinition,
                $"definition '{unexposed.ToContentKey}' in active pack '{unexposed.ToPackKey}' is not exposed.",
                Decision: decision,
                Refusal: new PackInstallRefusal(PackInstallCodes.ActivateUnexposedDefinition,
                    ContentPointer(target.SeedItems, unexposed.FromContentKey)));
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
                return new(false, packKey, version,
                    PackInstallCodes.ActivateProviderSlotOccupied,
                    $"category slot '{target.ProviderSlot}' is already held by the active provider "
                        + $"pack '{incumbent}'; deactivate it before activating '{packKey}'.", Decision: decision);
            }
        }

        // (b) Cross-pack same-key collision (ADR 0129 D4/D5 — INSTALLED-based, the F4 gate). A content key
        //     this pack shares with ANOTHER installed pack must resolve to an owner (a declared dependency
        //     chain, or a recorded client choice) before EITHER goes live — else the projector would
        //     silently first-wins one. An UNRESOLVED shared key fails activation closed, naming the key +
        //     the other pack(s).
        var ownership = new Dictionary<string, string>(_store.GetKeyOwnership(tenant), StringComparer.Ordinal);
        foreach (var resolution in ownershipResolutions ?? new Dictionary<string, string>())
            ownership[resolution.Key] = resolution.Value;
        var collisions = PackCompositionConflicts.Detect(
            PackCompositionConflicts.ClaimsFromInstalled(_store.ListInstalled(tenant)),
            ownership);
        var unresolved = collisions.FirstOrDefault(c =>
            c.Resolution == PackKeyOwnershipResolution.RequiresChoice
            && c.ClaimingPackKeys.Contains(packKey, StringComparer.Ordinal));
        if (unresolved is not null)
        {
            var others = string.Join(", ", unresolved.ClaimingPackKeys.Where(k => !string.Equals(k, packKey, StringComparison.Ordinal)));
            return new(false, packKey, version,
                PackInstallCodes.ActivateUnresolvedCollision,
                $"content key '{unresolved.ContentKey}' is also shipped by installed pack(s) "
                    + $"[{others}] and no owning pack has been chosen; record an owning-pack choice (or "
                    + $"declare a dependency) before activating '{packKey}'.", Decision: decision);
        }

        return null;
    }

    /// <inheritdoc />
    public Task<PackActivationOutcome> ActivateAsync(
        PackInstallContext context, string packKey, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(new Activation(this, context.Tenant, packKey, version, context.Now, context.Principal,
            context.OwnershipResolutions, context.CorrelationId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<PackDeactivationOutcome> DeactivateAsync(
        PackInstallContext context, string packKey, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(new Deactivation(this, context.Tenant, packKey, version, context.Now, context.Principal),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<PackNarrowingOutcome> NarrowAsync(
        PackInstallContext context, string packKey, string contentKey, JsonNode overlayPatch,
        AuthorizationDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(overlayPatch);
        ArgumentNullException.ThrowIfNull(decision);
        return RunAsync(new Narrowing(this, context, packKey, contentKey, overlayPatch, decision), cancellationToken);
    }

    // ck-10 S5b (DES-0029): activation, deactivation and narrowing each run their six ADR 0038 stages through
    // the kernel executor. A refusal that returns an audited outcome is thrown from its stage as Refused, so no
    // later stage runs, and the caller receives that same outcome.
    private async Task<TOutcome> RunAsync<TBound, TMutation, TSealed, TOutcome>(
        KernelWrite<TBound, TMutation, TSealed, TOutcome> write, CancellationToken cancellationToken)
        where TBound : class
        where TOutcome : class
    {
        try
        {
            return await WritePipeline.RunAsync(write, _pipelineObserver, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Kernel write pipeline did not react.");
        }
        catch (Refused refused) when (refused.Outcome is TOutcome outcome)
        {
            return outcome;
        }
    }

    /// <summary>An audited refusal thrown from the stage that refused; it carries the caller's outcome.</summary>
#pragma warning disable CA1032, CA1064 // A private control-flow signal, never seen outside this class.
    private sealed class Refused(object outcome) : Exception
#pragma warning restore CA1032, CA1064
    {
        public object Outcome { get; } = outcome;
    }

    /// <summary>
    /// Pack activation as its six ADR 0038 stages. Authorize keeps the audited pre-decision refusals. Bind reads
    /// the target version and the current active version. Validate runs the platform-requirement check and the
    /// composition and ownership refusal. Commit is the pointer flip and the projection inside one projection
    /// transaction, re-checking the premises other packs can change while the write lease excludes them.
    /// React is the audit, the post-commit observers and the outcome.
    /// </summary>
    private sealed class Activation(
        PackInstaller installer,
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string? actingPrincipal,
        IReadOnlyDictionary<string, string>? ownershipResolutions,
        Guid? correlationId)
        : KernelWrite<ActivationBound, ActivationBound, ActivationBound, PackActivationOutcome>
    {
        private AuthorizationDecision decision = null!;
        private string principal = null!;
        private PackActivationOutcome outcome = null!;
        private PackInstallAuditEntry activated = null!;
        private Func<Task<AggregateException?>>? observers;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(packKey))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.RefusedBlankPackKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.RefusedBlankVersion);
                ArgumentException.ThrowIfNullOrWhiteSpace(version);
            }

            // Ticket 151 cluster: the Draft/Inactive → Active pointer flip is what makes seed content LIVE
            // — the more consequential mutation — so the domain layer requires the acting principal exactly
            // as Install does. Refused + audited, fail-closed.
            if (string.IsNullOrWhiteSpace(actingPrincipal))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.ActivateRefusedNoPrincipal);
                ArgumentException.ThrowIfNullOrWhiteSpace(actingPrincipal);
            }

            principal = actingPrincipal;
            decision = installer.AuthorizeOrAudit(tenant, principal, now, packKey, version, correlationId);
            return ValueTask.CompletedTask;
        }

        /// <summary>The target must be installed: it is read before any guard inspects its manifest state.</summary>
        protected override ValueTask<ActivationBound?> BindAsync(CancellationToken ct)
        {
            var target = installer._store.GetVersion(tenant, packKey, version);
            var expectedActiveVersion = installer._store.GetActive(tenant, packKey)?.Version;
            if (target is null)
            {
                throw new Refused(installer.AuditActivationRefusal(tenant, packKey, version, now, principal,
                    PackInstallCodes.ActivateNotInstalled, null, decision));
            }
            return ValueTask.FromResult<ActivationBound?>(new ActivationBound(target, expectedActiveVersion));
        }

        /// <summary>The pointer flip is the whole mutation: activation derives nothing from the bound version.</summary>
        protected override ValueTask<ActivationBound> MutateAsync(ActivationBound bound, CancellationToken ct) =>
            ValueTask.FromResult(bound);

        protected override ValueTask<ActivationBound> ValidateAsync(
            ActivationBound bound, ActivationBound mutation, CancellationToken ct)
        {
            var unmetRequirements = PackPlatformRequirementCheck.FindUnmet(mutation.Target, installer._platform);
            if (unmetRequirements.Count > 0)
            {
                var first = unmetRequirements[0];
                throw new Refused(installer.AuditActivationRefusal(tenant, packKey, version, now, principal,
                    PackInstallCodes.ActivateUnmetPlatformRequirement,
                    $"capability '{first.Capability}' declared by '{first.DeclaredBy}' is unmet "
                        + $"({first.Failure}).", decision));
            }

            var compositionRefusal = installer.FindActivationCompositionRefusal(
                tenant, mutation.Target, ownershipResolutions, decision);
            if (compositionRefusal is not null)
            {
                throw new Refused(installer.AuditActivationRefusal(tenant, packKey, version, now, principal,
                    compositionRefusal.Error!, compositionRefusal.Detail, decision, compositionRefusal.Refusal));
            }
            return ValueTask.FromResult(mutation);
        }

        protected override ValueTask CommitAsync(ActivationBound validated, CancellationToken ct)
        {
            var authority = new PackProjectionAuthority(decision, packKey, version, tenant, new ActorId(principal), now);
            activated = new PackInstallAuditEntry(tenant, PackInstallAuditAction.Activated, packKey, version, now, null, null,
                "pack.install.activated", ActingPrincipal: principal);
            PackProjectionTransaction? transaction = null;
            try
            {
                using (transaction = new PackProjectionTransaction(ct))
                {
                    var target = installer._store.GetVersion(tenant, packKey, version);
                    if (!string.Equals(installer._store.GetActive(tenant, packKey)?.Version, validated.ExpectedActiveVersion, StringComparison.Ordinal))
                    {
                        outcome = new(false, packKey, version, PackInstallCodes.ActivateConcurrentChange, Decision: decision);
                    }
                    else if (target is null)
                    {
                        outcome = new(false, packKey, version, PackInstallCodes.ActivateNotInstalled, Decision: decision);
                    }
                    else if (installer.FindActivationCompositionRefusal(tenant, target, ownershipResolutions, decision) is var refusal
                        && refusal is not null)
                    {
                        // Other packs can change these premises without changing this pack's old version.
                        // The same guard is authoritative only while the writer excludes those changes.
                        outcome = refusal;
                    }
                    else
                    {
                        transaction.Enlist(installer._mutations);
                        transaction.Enlist(installer._projectionStore);
                        transaction.Enlist(installer._projector);
                        foreach (var resolution in ownershipResolutions ?? new Dictionary<string, string>())
                            installer._mutations.RecordKeyOwnership(tenant, resolution.Key, resolution.Value);
                        installer.ProjectionStore().ActivateAndRecordProjectionAdmission(tenant, packKey, version,
                            Admission(authority) with { Audit = new PackCommitAudit([activated], decision) });
                        var result = installer._projector?.Project(authority, ct);
                        if (Admitted(result))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (installer._projector is not null) installer.ProjectionStore().MarkProjectionCompleted(authority.Nonce);
                            transaction.Commit();
                            outcome = new(true, packKey, version, null, Projected: installer._projector is not null,
                                ProjectionResult: result, Decision: decision);
                        }
                        else
                        {
                            outcome = new(false, packKey, version, PackInstallCodes.ActivateProjectionRefused,
                                ProjectionResult: result, Decision: decision,
                                Refusal: (result as IPackProjectionRefusalReport)?.FirstRefusal);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = new(false, packKey, version,
                    exception is PackTransitionStateException ? PackInstallCodes.ActivateNotInstalled : PackInstallCodes.ActivateProjectionFailed,
                    Detail: exception.Message, Decision: decision);
            }
            finally { authority.Retire(); }
            observers = transaction is null ? null : transaction.ReactAsync;
            return ValueTask.CompletedTask;
        }

        protected override async ValueTask<PackActivationOutcome> ReactAsync(ActivationBound validated, CancellationToken ct)
        {
            // Audit and observers run only after the write lease and SQLite transaction have closed.
            // A notification failure cannot turn a committed activation into a reported rollback.
            try
            {
                installer._audit.AppendAuthorized(outcome.Activated
                    ? activated
                    : new PackInstallAuditEntry(tenant, PackInstallAuditAction.Refused, packKey, version, now, null, null,
                        outcome.Error ?? "pack.install.activated", ActingPrincipal: principal), decision);
            }
            catch (Exception exception) when (outcome.Activated)
            {
                outcome = outcome with { Detail = "Activation committed; audit notification failed: " + exception.Message };
            }
            if (outcome.Activated && observers is not null)
            {
                var diagnostics = await observers().ConfigureAwait(false);
                if (diagnostics is not null)
                    outcome = outcome with { Detail = string.IsNullOrEmpty(outcome.Detail)
                        ? diagnostics.Message : outcome.Detail + " " + diagnostics.Message };
            }
            return outcome;
        }
    }

    private sealed record ActivationBound(InstalledPack Target, string? ExpectedActiveVersion);

    /// <summary>
    /// Pack deactivation as its six ADR 0038 stages. Authorize keeps the audited pre-decision refusals. Bind reads
    /// the active version and the installed packs. Validate refuses a version that is not active or has active
    /// dependents. Commit re-checks both under the projection read lease, which excludes every activation's write
    /// lease (D4), and flips the pointer with its admission evidence. React is the audit, the projection and the
    /// outcome.
    /// </summary>
    private sealed class Deactivation(
        PackInstaller installer,
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string? actingPrincipal)
        : KernelWrite<DeactivationBound, DeactivationBound, DeactivationBound, PackDeactivationOutcome>
    {
        private AuthorizationDecision decision = null!;
        private string principal = null!;
        private string? refusal;
        private IReadOnlyList<string> dependents = [];
        private PackProjectionAuthority? projectionAuthority;
        private PackInstallAuditEntry? deactivated;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(packKey))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.RefusedBlankPackKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.RefusedBlankVersion);
                ArgumentException.ThrowIfNullOrWhiteSpace(version);
            }

            // Same posture as Activate: the reverse pointer flip is a consequential mutation — refuse +
            // audit an anonymous caller.
            if (string.IsNullOrWhiteSpace(actingPrincipal))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, version, now, actingPrincipal,
                    PackInstallCodes.DeactivateRefusedNoPrincipal);
                ArgumentException.ThrowIfNullOrWhiteSpace(actingPrincipal);
            }

            principal = actingPrincipal;
            decision = installer.AuthorizeOrAudit(tenant, principal, now, packKey, version);
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<DeactivationBound?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult<DeactivationBound?>(new DeactivationBound(
                installer._store.GetActive(tenant, packKey), installer._store.ListInstalled(tenant)));

        /// <summary>The pointer flip is the whole mutation: deactivation derives nothing from the bound state.</summary>
        protected override ValueTask<DeactivationBound> MutateAsync(DeactivationBound bound, CancellationToken ct) =>
            ValueTask.FromResult(bound);

        protected override ValueTask<DeactivationBound> ValidateAsync(
            DeactivationBound bound, DeactivationBound mutation, CancellationToken ct)
        {
            var premiseRefusal = DeactivationRefusal(mutation.Active, mutation.Installed, out var activeDependents);
            if (premiseRefusal is not null)
            {
                throw new Refused(installer.AuditDeactivationRefusal(
                    tenant, packKey, version, now, principal, premiseRefusal, decision, activeDependents));
            }
            return ValueTask.FromResult(mutation);
        }

        protected override ValueTask CommitAsync(DeactivationBound validated, CancellationToken ct)
        {
            // The dependents check and the pointer flip share one read lease, which excludes every activation's
            // write lease: no dependent can activate over this pack between the check and the flip (D4).
            using (PackProjectionActivationBarrier.Read(ct))
            {
                refusal = DeactivationRefusal(
                    installer._store.GetActive(tenant, packKey), installer._store.ListInstalled(tenant), out dependents);
                if (refusal is null)
                {
                    try
                    {
                        var candidate = new PackProjectionAuthority(
                            decision, packKey, version, tenant, new ActorId(principal), now);
                        var entry = new PackInstallAuditEntry(
                            tenant, PackInstallAuditAction.Deactivated, packKey, version, now, null, null,
                            "pack.install.deactivated", ActingPrincipal: principal);
                        installer.ProjectionStore().DeactivateAndRecordProjectionAdmission(
                            tenant, packKey, version, Admission(candidate) with { Audit = new PackCommitAudit([entry], decision) });
                        projectionAuthority = candidate;
                        deactivated = entry;
                    }
                    catch (PackTransitionStateException)
                    {
                        refusal = PackInstallCodes.DeactivateNotActive;
                    }
                }
            }
            return ValueTask.CompletedTask;
        }

        protected override async ValueTask<PackDeactivationOutcome> ReactAsync(DeactivationBound validated, CancellationToken ct)
        {
            if (refusal is not null)
            {
                return installer.AuditDeactivationRefusal(
                    tenant, packKey, version, now, principal, refusal, decision, dependents);
            }

            installer._audit.AppendAuthorized(deactivated!, decision);
            var outcome = new PackDeactivationOutcome(true, packKey, version, null, Decision: decision);
            return await installer.ProjectAsync(outcome, projectionAuthority!).ConfigureAwait(false);
        }

        private string? DeactivationRefusal(
            InstalledPack? active, IReadOnlyList<InstalledPack> installed, out IReadOnlyList<string> activeDependents)
        {
            activeDependents = [];
            if (active is null || !string.Equals(active.Version, version, StringComparison.Ordinal))
                return PackInstallCodes.DeactivateNotActive;
            activeDependents = ActiveDependents(installed, packKey);
            return activeDependents.Count > 0 ? PackInstallCodes.DeactivateDependentsActive : null;
        }
    }

    private sealed record DeactivationBound(InstalledPack? Active, IReadOnlyList<InstalledPack> Installed);

    /// <summary>
    /// A tenant narrowing as its six ADR 0038 stages. Authorize checks the carried decision against this pack,
    /// tenant, principal and instant. Bind reads the active version, the narrowed item and, for cascade defaults,
    /// the stored overrides. Mutate derives the override row and the composed cascade. Validate refuses a widening
    /// and runs the cascade admission over exactly that row. Commit saves it. React is the audit and the outcome.
    /// </summary>
    private sealed class Narrowing(
        PackInstaller installer,
        PackInstallContext context,
        string packKey,
        string contentKey,
        JsonNode overlayPatch,
        AuthorizationDecision decision)
        : KernelWrite<NarrowingBound, NarrowingMutation, PackTenantOverride, PackNarrowingOutcome>
    {
        private readonly TenantId tenant = context.Tenant;
        private readonly DateTimeOffset now = context.Now;
        private readonly string? principal = context.Principal;
        private InstalledPack active = null!;
        private PackInstallAuditEntry narrowed = null!;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(packKey))
            {
                installer.AuditPreDecisionRefusal(tenant, packKey, null, now, principal, PackInstallCodes.RefusedBlankPackKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
            }
            if (string.IsNullOrWhiteSpace(contentKey))
            {
                installer.AuditPreDecisionRefusal(
                    tenant, packKey, null, now, principal, PackInstallCodes.NarrowUnknownContentKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
            }

            // Same posture as Activate/Deactivate: a narrowing changes what the tenant's live definitions
            // say, so the domain layer itself requires the server-derived acting principal.
            if (string.IsNullOrWhiteSpace(principal))
            {
                installer.AuditPreDecisionRefusal(
                    tenant, packKey, null, now, principal, PackInstallCodes.NarrowRefusedNoPrincipal);
                ArgumentException.ThrowIfNullOrWhiteSpace(principal);
            }

            // The caller's guard owns the decision; validate its evidence before reading or mutating state.
            decision.RequireAllowedReaction(
                AuthorizationOperation.Parse(Permission.PackagesOperate), tenant, "pack", packKey);
            RequireMatchingContext(decision, principal, now);
            return ValueTask.CompletedTask;
        }

        private static void RequireMatchingContext(AuthorizationDecision decision, string principal, DateTimeOffset now)
        {
            if (decision.Request.Principal != new ActorId(principal) || decision.Request.At != now)
                throw new ArgumentException("The narrowing context must match the admitting decision.", nameof(decision));
        }

        protected override ValueTask<NarrowingBound?> BindAsync(CancellationToken ct)
        {
            var current = installer._store.GetActive(tenant, packKey);
            if (current is null)
            {
                throw new Refused(installer.AuditNarrowingRefusal(
                    tenant, packKey, contentKey, now, principal, PackInstallCodes.NarrowNotActive, null, decision));
            }
            active = current;

            var item = current.SeedItems.FirstOrDefault(seed => string.Equals(seed.Key, contentKey, StringComparison.Ordinal));
            if (item is null)
            {
                throw new Refused(installer.AuditNarrowingRefusal(
                    tenant, packKey, contentKey, now, principal,
                    PackInstallCodes.NarrowUnknownContentKey, null, decision));
            }

            var overrides = item.Kind == PackContentKind.CascadeDefaults
                ? installer._store.GetOverrides(tenant, packKey)
                : null;
            return ValueTask.FromResult<NarrowingBound?>(new NarrowingBound(current, item, overrides));
        }

        protected override ValueTask<NarrowingMutation> MutateAsync(NarrowingBound bound, CancellationToken ct)
        {
            var row = new PackTenantOverride(contentKey, overlayPatch.DeepClone());
            if (bound.Overrides is null)
                return ValueTask.FromResult(new NarrowingMutation(row, null));

            var patches = bound.Overrides.ToDictionary(stored => stored.ContentKey, stored => stored.OverlayPatch, StringComparer.Ordinal);
            patches[contentKey] = row.OverlayPatch;
            var composed = bound.Active.SeedItems.Where(seed => seed.Kind == PackContentKind.CascadeDefaults).Select(seed =>
            {
                var json = patches.TryGetValue(seed.Key, out var patch)
                    ? Harborline.Api.Foundation.Catalog.Templates.TemplateMerger.ApplyMergePatch(seed.ParseContent(), patch)?.ToJsonString() ?? "null"
                    : seed.CanonicalJson;
                return new PackComposedItem(packKey, seed.Key, seed.Kind, seed.Version, json, SeedCanonicalJson: seed.CanonicalJson);
            }).ToArray();
            return ValueTask.FromResult(new NarrowingMutation(row, composed));
        }

        protected override ValueTask<PackTenantOverride> ValidateAsync(
            NarrowingBound bound, NarrowingMutation mutation, CancellationToken ct)
        {
            if (!PackTenantNarrowing.IsNarrowing(bound.Item.ParseContent(), mutation.Row.OverlayPatch, out var wideningPath))
            {
                throw new Refused(installer.AuditNarrowingRefusal(
                    tenant, packKey, contentKey, now, principal,
                    PackTenantNarrowing.WideningRefusedCode, wideningPath, decision));
            }

            if (mutation.Composed is not null)
            {
                var admitted = installer._admission.Admit(mutation.Composed, tenant);
                if (!admitted.IsAdmissible)
                {
                    throw new Refused(installer.AuditNarrowingRefusal(tenant, packKey, contentKey, now, principal,
                        admitted.Refusals[0].Code, admitted.Refusals[0].Pointer, decision));
                }
            }
            return ValueTask.FromResult(mutation.Row);
        }

        protected override ValueTask CommitAsync(PackTenantOverride validated, CancellationToken ct)
        {
            // T-1048b: the narrowing's audit rides the save, so a durable store stages it in the same commit.
            narrowed = new PackInstallAuditEntry(
                tenant, PackInstallAuditAction.Narrowed, packKey, active.Version, now, null, null,
                $"pack.install.narrowed:{contentKey}",
                ActingPrincipal: principal);
            installer._mutations.SaveOverride(
                tenant, packKey, validated with { Audit = new PackCommitAudit([narrowed], decision) });
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<PackNarrowingOutcome> ReactAsync(PackTenantOverride validated, CancellationToken ct)
        {
            installer._audit.AppendAuthorized(narrowed, decision);
            return ValueTask.FromResult(new PackNarrowingOutcome(true, packKey, contentKey, Decision: decision));
        }
    }

    private sealed record NarrowingBound(InstalledPack Active, PackSeedItem Item, IReadOnlyList<PackTenantOverride>? Overrides);

    private sealed record NarrowingMutation(PackTenantOverride Row, PackComposedItem[]? Composed);

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

    Task IPackProjectionReconciler.ReconcilePendingAsync(CancellationToken cancellationToken) =>
        WritePipeline.RunAsync(new Reconciliation(this), _pipelineObserver, cancellationToken).AsTask();

    /// <summary>
    /// One startup reconciliation pass as its six ADR 0038 stages (ck-10 S5c). There is no live decision: each
    /// pending admission is the stored, already-authorized evidence of its transition, replayed rather than
    /// re-decided. Authorize requires the composed projector. Bind takes the installer's admission store. Commit
    /// reads the incomplete admissions, replays each as a projection authority, projects it, and marks it
    /// completed only when the pass refused nothing. React reports how many it completed.
    /// </summary>
    private sealed class Reconciliation(PackInstaller installer)
        : KernelWrite<IPackProjectionAdmissionStore, IPackProjectionAdmissionStore, IPackProjectionAdmissionStore, int>
    {
        private IPackProjectionDispatcher projector = null!;
        private int completed;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            var composed = installer._projector;
            if (composed is null)
                throw new InvalidOperationException("Pack projection is not composed.");
            projector = composed;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<IPackProjectionAdmissionStore?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult<IPackProjectionAdmissionStore?>(installer.ProjectionStore());

        protected override ValueTask<IPackProjectionAdmissionStore> MutateAsync(
            IPackProjectionAdmissionStore bound, CancellationToken ct) => ValueTask.FromResult(bound);

        protected override ValueTask<IPackProjectionAdmissionStore> ValidateAsync(
            IPackProjectionAdmissionStore bound, IPackProjectionAdmissionStore mutation, CancellationToken ct) =>
            ValueTask.FromResult(mutation);

        protected override ValueTask CommitAsync(IPackProjectionAdmissionStore validated, CancellationToken ct)
        {
            foreach (var tenantAdmissions in validated.ListIncompleteProjectionAdmissions()
                         .GroupBy(admission => admission.Tenant)
                         .OrderBy(group => group.Key.Value, StringComparer.Ordinal))
            {
                foreach (var admission in tenantAdmissions)
                {
                    ct.ThrowIfCancellationRequested();
                    var authority = PackProjectionAuthority.FromAdmission(admission);
                    try
                    {
                        var result = projector.Project(authority, ct);
                        if (Admitted(result))
                        {
                            validated.MarkProjectionCompleted(admission.AdmissionId);
                            completed++;
                        }
                    }
                    finally
                    {
                        authority.Retire();
                    }
                }
            }
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<int> ReactAsync(IPackProjectionAdmissionStore validated, CancellationToken ct) =>
            ValueTask.FromResult(completed);
    }

    /// <summary>Projects a committed deactivation, then retires its one-shot authority whatever happened.</summary>
    private async Task<PackDeactivationOutcome> ProjectAsync(
        PackDeactivationOutcome outcome,
        PackProjectionAuthority authority)
    {
        try
        {
            if (_projector is null)
                return outcome;
            // The deactivation has committed: the projection runs whatever the caller's token says, as before.
            return await WritePipeline.RunAsync(new Projection(this, outcome, authority), _pipelineObserver, CancellationToken.None)
                .ConfigureAwait(false) ?? outcome;
        }
        finally
        {
            authority.Retire();
        }
    }

    /// <summary>
    /// The projection a deactivation's react creates, as its six ADR 0038 stages (ck-10 S5c). Authorize requires
    /// the live allowed decision the authority was minted from. Bind takes the composed projector (with none the
    /// caller skips the write and the outcome is unchanged). Commit projects and marks the admission completed only when
    /// the pass refused nothing; a projection failure is carried to the outcome, never thrown. React records the
    /// result on the outcome.
    /// </summary>
    private sealed class Projection(
        PackInstaller installer,
        PackDeactivationOutcome outcome,
        PackProjectionAuthority authority)
        : KernelWrite<IPackProjectionDispatcher, PackProjectionAuthority, PackProjectionAuthority, PackDeactivationOutcome>
    {
        private IPackProjectionDispatcher projector = null!;
        private object? result;
        private Exception? failure;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            var decision = authority.Decision;
            if (decision is null)
                throw new InvalidOperationException("A live pack projection requires the decision that admitted it.");
            decision.RequireAllowed();
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<IPackProjectionDispatcher?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult(installer._projector);

        protected override ValueTask<PackProjectionAuthority> MutateAsync(IPackProjectionDispatcher bound, CancellationToken ct)
        {
            projector = bound;
            return ValueTask.FromResult(authority);
        }

        protected override ValueTask<PackProjectionAuthority> ValidateAsync(
            IPackProjectionDispatcher bound, PackProjectionAuthority mutation, CancellationToken ct) =>
            ValueTask.FromResult(mutation);

        protected override ValueTask CommitAsync(PackProjectionAuthority validated, CancellationToken ct)
        {
            try
            {
                result = projector.Project(validated, CancellationToken.None);
                if (Admitted(result))
                    installer.ProjectionStore().MarkProjectionCompleted(validated.Nonce);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = exception;
            }
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<PackDeactivationOutcome> ReactAsync(PackProjectionAuthority validated, CancellationToken ct) =>
            ValueTask.FromResult(failure is null
                ? outcome with { Projected = true, ProjectionResult = result }
                : outcome with { ProjectionResult = failure });
    }

    /// <summary>
    /// A pass that refused an item leaves its admission INCOMPLETE, so the next boot's
    /// <see cref="IPackProjectionReconciler.ReconcilePendingAsync"/> re-runs it. Only a pass that refused
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
        string version,
        Guid? correlationId = null)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("Correlation ID must be non-empty.", nameof(correlationId));
        var scope = ScopeExpression.Parse($"/records/{packKey}");
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            new ActorId(principal),
            tenant,
            new AuthorizationTarget("pack", packKey, scope),
            at) { CorrelationId = correlationId };
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
        ClaimedPackCoordinates? claimedCoordinates = null,
        bool collectRefusals = false,
        InstallBound? bound = null)
    {
        var verify = _verifier.Verify(packBytes, context.TrustStore);
        var claimed = claimedCoordinates ?? ReadClaimedCoordinates(packBytes);

        // (S-7) verify-before-effect: only a Verified verdict exposes the manifest/content.
        if (verify.Verdict != PackVerdict.Verified || verify.Manifest is null || verify.Contents is null)
        {
            var failedVerificationRevocationStale =
                context.Revocation.IsStale(context.Now, context.RevocationMaxAge);
            // A plain local, not a pattern variable, so Stryker can instrument the condition (no CompileError rollback).
            var kindPointer = verify.FailurePointer;
            if (kindPointer is not null
                && verify.Details.Contains(PackVerificationCodes.ContentKindUnknown))
            {
                // T-981: name the unclassifiable kind and where it is, not a generic not_verified.
                return HardRefusal(claimed.PackKey, claimed.Version,
                    PackInstallCodes.RefusedUnknownContentKind, failedVerificationRevocationStale,
                    refusals: [new PackInstallRefusal(PackInstallCodes.RefusedUnknownContentKind, kindPointer)]);
            }

            return HardRefusal(claimed.PackKey, claimed.Version,
                PackInstallCodes.RefusedNotVerified, failedVerificationRevocationStale);
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
            return HardRefusal(manifest.Key, manifest.Version, PackInstallCodes.RefusedRevoked, revocationStale,
                signerB64, epoch, scope);
        }

        // (S-13) scope-vs-content — structural fail-closed extension point (no v1 first-party scope refuses).
        if (scope is { } vouch && !PackScopePolicy.MaySeed(vouch))
        {
            return HardRefusal(manifest.Key, manifest.Version, PackInstallCodes.RefusedScope, revocationStale,
                signerB64, epoch, scope);
        }

        // Explicitly classify every verified item before the remaining admission gates. The preview
        // exposes the resulting destinations, so this is an admission decision rather than a
        // feature-graph read-model-only projection (T-565).
        var destinationClassifications = PackDestinationClassifier.Classify(contents);

        // A verified declaration is not enough: content must have a live projection path in this build.
        // NavWorkspaceConfig, Layout, Resource, and Bookable are intentionally absent because their
        // consumers interpret active immutable declarative seeds directly; no executable projection is
        // permitted for them (S-3). Standards never travel; cascade defaults require a consumer.
        var earlyRefusals = PackCompiledShapeCheck.FindRefusals(contents).ToList();
        earlyRefusals.AddRange(PackTransportRuleCheck.FindRefusals(contents));
        earlyRefusals.AddRange(UnsupportedCascadeDefaultsRefusals(contents));
        if (!collectRefusals && earlyRefusals.Count > 0)
        {
            // Preserve install's established priority (standards, then cascade),
            // independent of the authoring order in the export. CHECK collects the whole list below.
            var refusal = earlyRefusals.MinBy(refusal => refusal.Code switch
            {
                PackInstallCodes.RefusedCompiledShapeReplacement => -1,
                PackInstallCodes.RefusedUnsupportedStandardsCatalog => 0,
                PackInstallCodes.RefusedUnsupportedCascadeDefaults => 1,
                _ => 2,
            })!;
            return refusal.Code switch
            {
                PackInstallCodes.RefusedCompiledShapeReplacement => HardRefusal(
                    manifest.Key, manifest.Version, PackInstallCodes.RefusedCompiledShapeReplacement,
                    revocationStale, signerB64, epoch, scope, refusals: [refusal],
                    destinationClassifications: destinationClassifications),
                PackInstallCodes.RefusedUnsupportedStandardsCatalog => HardRefusal(
                    manifest.Key, manifest.Version, PackInstallCodes.RefusedUnsupportedStandardsCatalog,
                    revocationStale, signerB64, epoch, scope, refusals: [refusal],
                    destinationClassifications: destinationClassifications),
                _ => HardRefusal(
                    manifest.Key, manifest.Version, PackInstallCodes.RefusedUnsupportedCascadeDefaults,
                    revocationStale, signerB64, epoch, scope, refusals: [refusal],
                    destinationClassifications: destinationClassifications),
            };
        }

        // T-572 S4 (kernel-core-ck-8): a pass-through platform item whose declared contract is outside the
        // seed's window is refused before any installed-state read. CHECK collects it with the rest below.
        var contractRefusals = PackContractWindowCheck.FindRefusals(contents, PlatformPackageSeed.ContractWindow);
        if (!collectRefusals && contractRefusals.Count > 0)
        {
            return HardRefusal(
                manifest.Key, manifest.Version, PackInstallCodes.RefusedContractOutOfWindow, revocationStale,
                signerB64, epoch, scope, refusals: contractRefusals,
                destinationClassifications: destinationClassifications);
        }

        var unmetRequirements = PackPlatformRequirementCheck.FindUnmet(manifest, contents, _platform);
        var platformRefusals = unmetRequirements
            .Select(requirement => new PackInstallRefusal(
                requirement.Failure == PackPlatformRequirementFailure.MissingCapability
                    ? PackInstallCodes.RefusedMissingPlatformCapability
                    : PackInstallCodes.RefusedPlatformVersionFloor,
                string.Equals(requirement.DeclaredBy, manifest.Key, StringComparison.Ordinal)
                    ? "/"
                    : ContentPointer(contents, requirement.DeclaredBy)))
            .ToList();
        if (!collectRefusals && unmetRequirements.Count > 0 && unmetRequirements[0].Failure == PackPlatformRequirementFailure.MissingCapability)
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                PackInstallCodes.RefusedMissingPlatformCapability,
                revocationStale,
                signerB64,
                epoch,
                scope,
                unmetRequirements,
                PlatformRequirementRefusals(
                    PackInstallCodes.RefusedMissingPlatformCapability, manifest, contents, unmetRequirements));
        }

        if (!collectRefusals && unmetRequirements.Count > 0)
        {
            return HardRefusal(
                manifest.Key,
                manifest.Version,
                PackInstallCodes.RefusedPlatformVersionFloor,
                revocationStale,
                signerB64,
                epoch,
                scope,
                unmetRequirements,
                PlatformRequirementRefusals(
                    PackInstallCodes.RefusedPlatformVersionFloor, manifest, contents, unmetRequirements));
        }

        // ONE install-state snapshot serves all four installed-state checks below (prior version,
        // cross-pack collisions, content references, dependency presence).
        // Install's bind stage has already read the installed versions and this key's watermark.
        var bindsThisKey = bound is not null && string.Equals(bound.PackKey, manifest.Key, StringComparison.Ordinal);
        var installed = bound?.Installed ?? _store.ListInstalled(context.Tenant);
        var prior = installed.FirstOrDefault(pack => pack.PackKey == manifest.Key && pack.Lifecycle == PackLifecycleState.Active)
            ?? LatestInstalled(installed, manifest.Key);
        var watermark = bindsThisKey ? bound!.Watermark : _store.GetWatermark(context.Tenant, manifest.Key);
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
        var priorOverrides = bindsThisKey ? bound!.Overrides : _store.GetOverrides(context.Tenant, manifest.Key);
        var reattach = PackReattachPlanner.Plan(priorSeeds, contents, priorOverrides, manifest.RenamedFrom);
        if (reattach.RefusalCode is not null)
        {
            // (T-655) The platform's safety-floor producer refused the whole re-attach — a floor member is
            // present but not an integer. Carry its code and named member out rather than installing with
            // the overrides silently dropped.
            return HardRefusal(
                manifest.Key, manifest.Version, reattach.RefusalCode, revocationStale, signerB64, epoch, scope,
                refusals:
                [
                    new PackInstallRefusal(
                        reattach.RefusalCode,
                        $"/{PackageSafetyFloorReattachment.FloorsMember}/{reattach.RefusalMember}"),
                ]);
        }

        // (S-9) ADR 0143 admission over the COMPOSED post-install cascade (seed ⊕ re-attached overrides).
        var composed = BuildComposed(manifest.Key, contents, reattach.Reattached, manifest.CapabilityRequirements);
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

        // (K9, T-152 D7) Re-derive the cross-package edges from the verified content, whatever the manifest's
        // ContentReferences claim: an edge into a package the manifest does not declare is refused.
        var undeclaredReferences = DetectUndeclaredReferences(manifest, contents);

        // (Ticket 152) Pack-level declared-dependency PRESENCE check: every manifest.Dependencies entry
        // must resolve to an installed pack at the pinned-or-newer version. The single-level-only rule
        // (A9, PackValidator) is untouched — this checks the one level v1 declares actually EXISTS.
        var unmetDependencies = DetectUnmetDependencies(installed, manifest);

        // Verdict.
        PackInstallVerdict verdict;
        IReadOnlyList<string> refusalCodes;
        IReadOnlyList<PackInstallRefusal> refusals;
        if (collectRefusals)
        {
            var collected = new List<PackInstallRefusal>(earlyRefusals);
            collected.AddRange(contractRefusals);
            collected.AddRange(platformRefusals);
            collected.AddRange(admission.Refusals.Select(refusal => new PackInstallRefusal(
                PackInstallCodes.RefusedAdmission,
                ContentPointer(contents, refusal.ContentKey) + refusal.Pointer)));
            collected.AddRange(undeclaredReferences.Select(reference => new PackInstallRefusal(
                PackInstallCodes.RefusedUndeclaredReference,
                ContentPointer(contents, reference.FromContentKey))));
            collected.AddRange(unmetReferences.Select(reference => new PackInstallRefusal(
                PackInstallCodes.RefusedUnmetContentReference,
                ContentPointer(contents, reference.FromContentKey))));
            collected.AddRange(unmetDependencies.Select(dependency => new PackInstallRefusal(
                dependency.MalformedPin
                    ? PackInstallCodes.RefusedMalformedDependencyPin
                    : PackInstallCodes.RefusedUnmetDependency,
                DependencyPointer(manifest, dependency))));
            collected.AddRange(watermarkHits.Select(hit => new PackInstallRefusal(
                hit.Kind == PackWatermarkHitKind.VersionDowngrade
                    ? PackInstallCodes.RefusedDowngrade
                    : PackInstallCodes.RefusedFloorWeakened,
                "/")));

            refusalCodes = collected.Select(refusal => refusal.Code).Distinct().ToList();
            refusals = collected;
            verdict = collected.Any(refusal =>
                refusal.Code is not PackInstallCodes.RefusedDowngrade and not PackInstallCodes.RefusedFloorWeakened)
                ? PackInstallVerdict.Refused
                : watermarkHits.Count > 0
                    ? PackInstallVerdict.RequiresBreakGlass
                    : isUpgrade ? PackInstallVerdict.WouldUpgrade : PackInstallVerdict.WouldInstall;
        }
        else if (!admission.IsAdmissible)
        {
            verdict = PackInstallVerdict.Refused;
            refusalCodes = new[] { PackInstallCodes.RefusedAdmission };
            refusals = admission.Refusals
                .Select(refusal => new PackInstallRefusal(
                    PackInstallCodes.RefusedAdmission,
                    ContentPointer(contents, refusal.ContentKey) + refusal.Pointer))
                .ToList();
        }
        else if (undeclaredReferences.Count > 0)
        {
            // A package-boundary violation (K9) is a HARD refusal: declare the dependency and re-export.
            verdict = PackInstallVerdict.Refused;
            refusalCodes = new[] { PackInstallCodes.RefusedUndeclaredReference };
            refusals = undeclaredReferences
                .Select(reference => new PackInstallRefusal(
                    PackInstallCodes.RefusedUndeclaredReference,
                    ContentPointer(contents, reference.FromContentKey)))
                .ToList();
        }
        else if (unmetReferences.Count > 0)
        {
            // A missing dependency is a HARD refusal (not S-8 break-glass): you cannot wave through content
            // that binds a type no installed app provides — install the required app first.
            verdict = PackInstallVerdict.Refused;
            refusalCodes = new[] { PackInstallCodes.RefusedUnmetContentReference };
            refusals = unmetReferences
                .Select(reference => new PackInstallRefusal(
                    PackInstallCodes.RefusedUnmetContentReference,
                    ContentPointer(contents, reference.FromContentKey)))
                .ToList();
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
            refusals = unmetDependencies
                .Select(dependency => new PackInstallRefusal(
                    dependency.MalformedPin
                        ? PackInstallCodes.RefusedMalformedDependencyPin
                        : PackInstallCodes.RefusedUnmetDependency,
                    DependencyPointer(manifest, dependency)))
                .ToList();
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
            refusals = watermarkHits
                .Select(hit => new PackInstallRefusal(
                    hit.Kind == PackWatermarkHitKind.VersionDowngrade
                        ? PackInstallCodes.RefusedDowngrade
                        : PackInstallCodes.RefusedFloorWeakened,
                    "/"))
                .ToList();
        }
        else
        {
            verdict = isUpgrade ? PackInstallVerdict.WouldUpgrade : PackInstallVerdict.WouldInstall;
            refusalCodes = Array.Empty<string>();
            refusals = Array.Empty<PackInstallRefusal>();
        }

        // (ADR 0129 D4/D5 — F4) Cross-pack same-key collisions: does this candidate ship a content key an
        // already-installed OTHER pack also ships? SURFACED here (never silently first-wins-merged, S-2);
        // install stays additive, and an UNRESOLVED collision is enforced fail-closed at ACTIVATE.
        var crossPackCollisions = DetectCandidateCollisions(context.Tenant, installed, manifest, contents, bound?.KeyOwnership);

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
            UnmetPlatformRequirements: collectRefusals ? unmetRequirements : Array.Empty<PackUnmetPlatformRequirement>())
        {
            UnmetDependencies = unmetDependencies,
            Refusals = refusals,
            DestinationClassifications = destinationClassifications,
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

    private static InstallPlan HardRefusal(
        string packKey, string version, string code, bool revocationStale,
        string? signerB64 = null,
        long? epoch = null,
        Harborline.Api.Foundation.Packs.Trust.TrustScope? scope = null,
        IReadOnlyList<PackUnmetPlatformRequirement>? unmetPlatformRequirements = null,
        IReadOnlyList<PackInstallRefusal>? refusals = null,
        IReadOnlyList<PackContentDestination>? destinationClassifications = null)
    {
        var preview = new PackInstallPreview(
            PackInstallVerdict.Refused, packKey, version, signerB64, epoch, scope,
            IsUpgrade: false, PriorVersion: null,
            NewSeedKeys: Array.Empty<string>(),
            Conflicts: Array.Empty<PackReattachConflict>(),
            WatermarkHits: Array.Empty<PackWatermarkHit>(),
            AdmissionRefusals: Array.Empty<PackAdmissionRefusal>(),
            RevocationStale: revocationStale,
            RefusalCodes: [code],
            CrossPackCollisions: Array.Empty<PackCrossPackCollision>(),
            UnmetContentReferences: Array.Empty<PackUnmetContentReference>(),
            UnmetPlatformRequirements: unmetPlatformRequirements ?? Array.Empty<PackUnmetPlatformRequirement>())
        {
            Refusals = refusals ?? [new PackInstallRefusal(code, "/")],
            DestinationClassifications = destinationClassifications ?? Array.Empty<PackContentDestination>(),
        };
        return new InstallPlan(preview, null, epoch, null, null, null, PackInstallAuditAction.Refused);
    }

    private List<PackInstallRefusal> UnsupportedCascadeDefaultsRefusals(IReadOnlyList<PackContentItem> contents)
    {
        var refusals = new List<PackInstallRefusal>();
        for (var index = 0; index < contents.Count; index++)
        {
            if (contents[index].Kind == PackContentKind.CascadeDefaults
                && _admission is not IPackCascadeDefaultsAdmission { ConsumesCascadeDefaults: true })
            {
                refusals.Add(new PackInstallRefusal(
                    PackInstallCodes.RefusedUnsupportedCascadeDefaults,
                    ContentPointer(index)));
            }
        }

        return refusals;
    }

    private static List<PackInstallRefusal> PlatformRequirementRefusals(
        string code,
        PackManifest manifest,
        IReadOnlyList<PackContentItem> contents,
        IReadOnlyList<PackUnmetPlatformRequirement> unmetRequirements)
        => unmetRequirements
            .Select(requirement => new PackInstallRefusal(
                code,
                string.Equals(requirement.DeclaredBy, manifest.Key, StringComparison.Ordinal)
                    ? "/"
                    : ContentPointer(contents, requirement.DeclaredBy)))
            .ToList();

    private static string ContentPointer(IReadOnlyList<PackContentItem> contents, string contentKey)
    {
        for (var index = 0; index < contents.Count; index++)
        {
            if (string.Equals(contents[index].Key, contentKey, StringComparison.Ordinal)) return ContentPointer(index);
        }

        return "/";
    }

    private static string ContentPointer(int index) => $"/contents/{index}/contentBase64";

    private static string ContentPointer(IReadOnlyList<PackSeedItem> contents, string contentKey)
    {
        for (var index = 0; index < contents.Count; index++)
        {
            if (string.Equals(contents[index].Key, contentKey, StringComparison.Ordinal)) return ContentPointer(index);
        }

        return "/";
    }

    private static string DependencyPointer(PackManifest manifest, PackUnmetDependency dependency)
    {
        for (var index = 0; index < manifest.Dependencies.Count; index++)
        {
            var candidate = manifest.Dependencies[index];
            if (string.Equals(candidate.Key, dependency.DependencyKey, StringComparison.Ordinal)
                && string.Equals(candidate.Version, dependency.PinnedVersion, StringComparison.Ordinal))
            {
                return $"/dependencies/{index}";
            }
        }

        return "/";
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
        AuthorizationDecision decision,
        PackInstallRefusal? refusal = null)
    {
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Refused, packKey, version, now, null, null,
            Detail: detail is null ? error : $"{error}: {detail}", ActingPrincipal: actingPrincipal), decision);
        return new PackActivationOutcome(false, packKey, version, error, detail, Decision: decision, Refusal: refusal);
    }

    private PackDeactivationOutcome AuditDeactivationRefusal(
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string actingPrincipal,
        string error,
        AuthorizationDecision decision,
        IReadOnlyList<string>? dependents = null)
    {
        _audit.AppendAuthorized(new PackInstallAuditEntry(
            tenant, PackInstallAuditAction.Refused, packKey, version, now, null, null,
            Detail: dependents is { Count: > 0 } ? $"{error}: {string.Join(", ", dependents)}" : error,
            ActingPrincipal: actingPrincipal), decision);
        return new PackDeactivationOutcome(false, packKey, version, error, Decision: decision, Dependents: dependents ?? []);
    }

    /// <summary>The Active packs, ordinal order, whose declared closure runs through <paramref name="packKey"/>,
    /// transitively. Every other Active pack depends on the platform pack, which roots every closure (D5).</summary>
    private static string[] ActiveDependents(IReadOnlyList<InstalledPack> installed, string packKey)
    {
        var active = installed.Where(pack => pack.Lifecycle == PackLifecycleState.Active).ToList();
        var reached = new HashSet<string>(StringComparer.Ordinal) { packKey };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var pack in active)
                if (!reached.Contains(pack.PackKey) && (packKey == KernelPackageClosure.PlatformPackageKey
                        || pack.Dependencies.Any(dependency => reached.Contains(dependency.Key))))
                    grew = reached.Add(pack.PackKey);
        }
        reached.Remove(packKey);
        return reached.Order(StringComparer.Ordinal).ToArray();
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
        IReadOnlyList<PackContentItem> contents, IReadOnlyDictionary<string, string>? keyOwnership = null)
    {
        // Existing installed claims EXCLUDING the candidate's own key (a same-key clash with a prior version
        // of THIS pack is an upgrade — S-10 re-attach — not a cross-pack collision) plus the candidate.
        var claims = PackCompositionConflicts.ClaimsFromInstalled(installed, excludePackKey: manifest.Key);
        claims.Add(new PackKeyClaim(
            manifest.Key,
            contents.Select(c => new PackClaimedContent(c.Key, c.Kind)).ToList(),
            manifest.Dependencies.Select(d => d.Key).ToList()));
        return PackCompositionConflicts.Detect(claims, keyOwnership ?? _store.GetKeyOwnership(tenant))
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
    /// <summary>
    /// K9 at install (T-152 D7, ADR 0028): re-derives every content reference from the verified bodies through
    /// the same deriver the exporter runs, against the manifest's declared dependency keys, and returns the ones
    /// that resolve to neither this pack nor a declared dependency. The manifest's own
    /// <c>ContentReferences</c> are not consulted, so a hand-signed manifest cannot omit an edge to pass.
    /// </summary>
    private static IReadOnlyList<PackUndeclaredContentReference> DetectUndeclaredReferences(
        PackManifest manifest, IReadOnlyList<PackContentItem> contents)
    {
        var sources = new List<PackContentSource>(contents.Count);
        foreach (var item in contents)
        {
            // Definitely assigned before the try so Stryker can instrument this method (no Safe Mode rollback).
            JsonNode? body = null;
            try
            {
                body = JsonNode.Parse(item.CanonicalBytes.Span);
            }
            catch (System.Text.Json.JsonException)
            {
                // An unreadable body carries no reference; its own parser owns the refusal.
            }

            if (body is not null)
            {
                sources.Add(new PackContentSource(item.Key, item.Kind, item.Version, body));
            }
        }

        PackContentReferenceDeriver.Derive(
            manifest.Key, sources, manifest.Dependencies.Select(dependency => dependency.Key), out var undeclared);
        return undeclared;
    }

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
        IReadOnlyList<PackTenantOverride> reattached,
        IReadOnlyList<string> capabilityRequirements)
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
                packageKey, item.Key, item.Kind, item.Version, effective?.ToJsonString() ?? "null",
                System.Collections.Immutable.ImmutableArray.CreateRange(capabilityRequirements),
                Encoding.UTF8.GetString(item.CanonicalBytes.Span)));
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
            manifest.CapabilityRequirements,
            manifest.Exposes,
            manifest.InterfaceVersion);
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
