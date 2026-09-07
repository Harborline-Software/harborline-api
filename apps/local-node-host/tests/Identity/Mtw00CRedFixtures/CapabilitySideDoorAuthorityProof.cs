using Harborline.Api.Foundation.CapabilityAdmission;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>Production-bound proofs for the MTW-01F release-admission side-door fences.</summary>
internal static class CapabilitySideDoorAuthorityProof
{
    private static readonly CapabilityKey Key = new(
        CapabilityId.Of("identity.multi-tenant-web"),
        CapabilityVersion.Of("v1"));

    internal static void ProveOperatorControlIsDisableOnly()
    {
        var ready = State(compiled: true, admitted: true, ready: true);
        var refused = State(compiled: true, admitted: false, ready: false);

        Require(!RuntimeCapabilityDisableFence.Apply(ready, operatorDisabled: true, globallyDisabled: false).Ready,
            "The operator disable control did not veto a ready capability.");
        Require(!RuntimeCapabilityDisableFence.Apply(refused, operatorDisabled: false, globallyDisabled: false).Ready,
            "Clearing the operator veto enabled a capability refused by admission.");

        // BOTH vetoes engaged. Previously untested, and the gap was not academic: a mutation
        // returning Ready when operatorDisabled && globallyDisabled shipped green past every other
        // fixture here, because none of them ever called Apply with both flags set. Vetoes compose
        // as OR, so the combination must be at least as closed as either alone.
        Require(!RuntimeCapabilityDisableFence.Apply(ready, operatorDisabled: true, globallyDisabled: true).Ready,
            "Both vetoes engaged left a ready capability enabled.");
        Require(!RuntimeCapabilityDisableFence.Apply(refused, operatorDisabled: true, globallyDisabled: true).Ready,
            "Both vetoes engaged enabled a capability refused by admission.");

        // Every assertion above reads .Ready, which proves "cannot manufacture readiness" — NOT
        // "disable-only", which is what this method is named. A mutation that drops Ready while
        // RAISING Compiled and Admitted survived the whole suite: the veto would widen two of ADR
        // 0154 D3's four gate outcomes, and Admitted is public and feeds ComputeSnapshotVersion, so
        // a consumer gating on admission rather than readiness would inherit the escalation.
        // Non-escalation must therefore be asserted on every field, in every input combination.
        foreach (var before in new[] { ready, refused, State(compiled: true, admitted: true, ready: false) })
        {
            foreach (var (operatorDisabled, globallyDisabled) in
                     new[] { (true, false), (false, true), (true, true), (false, false) })
            {
                var after = RuntimeCapabilityDisableFence.Apply(before, operatorDisabled, globallyDisabled);
                Require(!(after.Compiled && !before.Compiled),
                    "The disable fence raised Compiled — the veto must never widen a gate outcome.");
                Require(!(after.Admitted && !before.Admitted),
                    "The disable fence raised Admitted — the veto must never widen a gate outcome.");
                Require(!(after.Ready && !before.Ready),
                    "The disable fence raised Ready — the veto must never widen a gate outcome.");
            }
        }
    }

    internal static void ProveRegistrationCannotManufactureAdmission()
    {
        var catalog = Catalog(registrations: [], probes: []);
        var state = catalog.SnapshotAsync().AsTask().GetAwaiter().GetResult().Find(Key);

        Require(state.Compiled && !state.Admitted && !state.Ready,
            "DI presence without the exact manifest registration was treated as admission.");

        var extra = new CapabilityKey(CapabilityId.Of("identity.side-door"), CapabilityVersion.Of("v1"));
        RequireThrows(() => Catalog(
            [new RuntimeCapabilityRegistration(extra, "local-node", NodeInventory())], []));
    }

    internal static void ProveCatalogHasNoPackOrTenantActivationInput()
    {
        // NonPublic included deliberately: RuntimeCapabilityCatalog has an internal constructor, and
        // the default BindingFlags would never read it. No violation hides there today — the internal
        // overload delegates to the public one with a strict subset of its parameters — but this is a
        // structural fence, and a later internal overload taking an operator or tenant input would
        // otherwise pass it in silence.
        var parameters = typeof(RuntimeCapabilityCatalog)
            .GetConstructors(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => $"{parameter.ParameterType.FullName}:{parameter.Name}")
            .ToArray();
        var forbidden = new[] { "pack", "tenant", "activation", "featureflag", "operator" };

        Require(!parameters.Any(parameter => forbidden.Any(token =>
                parameter.Contains(token, StringComparison.OrdinalIgnoreCase))),
            "The production catalog exposes a pack, tenant, feature-flag, or operator enable input.");

        var refused = Catalog([], []).SnapshotAsync().AsTask().GetAwaiter().GetResult().Find(Key);
        Require(!refused.Ready, "An absent pack/tenant input manufactured readiness.");
    }

    internal static void ProveGlobalDisableIsVetoOnly()
    {
        var ready = State(compiled: true, admitted: true, ready: true);
        var refused = State(compiled: false, admitted: false, ready: false);

        Require(!RuntimeCapabilityDisableFence.Apply(ready, operatorDisabled: false, globallyDisabled: true).Ready,
            "The global disable did not veto a ready capability.");
        Require(!RuntimeCapabilityDisableFence.Apply(refused, operatorDisabled: false, globallyDisabled: false).Ready,
            "Clearing global disable enabled an absent capability.");
    }

    private static RuntimeCapabilityCatalog Catalog(
        IEnumerable<RuntimeCapabilityRegistration> registrations,
        IEnumerable<ICapabilityReadinessProbe> probes) =>
        new(VerifiedManifest(), "local-node", registrations, probes);

    private static VerifiedReleaseCapabilityManifest VerifiedManifest()
    {
        var manifest = ReleaseCapabilityManifest.Create(
            ReleaseCapabilityManifest.SupportedSchemaVersion,
            "test-product",
            "test-channel",
            "mtw-01f-proof",
            DateTimeOffset.UnixEpoch,
            "test",
            "release:test",
            string.Empty,
            [
                ReleaseArtifactIdentity.Create("carrier.bundle", "test", "carrier.bundle", new string('a', 64), 1),
                ReleaseArtifactIdentity.Create("local-node", "test", "local-node", new string('b', 64), 1),
            ],
            [ReleaseCapabilityEntry.Create(Key, [
                ArtifactExecutableInventory.Create("carrier.bundle", ReferenceAppInventory()),
                ArtifactExecutableInventory.Create("local-node", NodeInventory()),
            ])]);
        return new VerifiedReleaseCapabilityManifest(manifest, "test");
    }

    private static ExecutableInventory ReferenceAppInventory() => new(
        routes: ["GET /login"], controls: ["identity.tenant-selector"], stateStores: ["identity.web-session"]);

    private static ExecutableInventory NodeInventory() => new(
        routes: ["GET /api/session"], commands: ["identity.switch-tenant"],
        handlers: ["identity.session-authority"], migrations: ["identity.sessions/v1"],
        bundledAliases: ["identity.session-recovery"], stateStores: ["identity.session-authority-store"],
        backgroundWorkers: ["identity.revocation-worker"], dataFamilies: ["identity.sessions"],
        caches: ["tenant-cache/v1"], searchIndexes: ["tenant-search/v1"], fileStores: ["tenant-files/v1"],
        reportExports: ["tenant-exports/v1"], auditStreams: ["tenant-audit/v1"], jobs: ["tenant-jobs/v1"]);

    private static RuntimeCapabilityState State(bool compiled, bool admitted, bool ready) =>
        new(Key, compiled, admitted, ready, ready ? [] : [CapabilityRefusalCodes.NotAdmitted]);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows(Action action)
    {
        try
        {
            action();
        }
        catch (CapabilityCatalogAdmissionException)
        {
            return;
        }

        throw new InvalidOperationException("An extra runtime registration bypassed the signed manifest.");
    }
}
