using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>
/// Read-only ADR 0154 runtime admission join. It accepts only a previously verified release manifest and
/// explicit host registrations/probes; it has no tenant, actor, feature-flag, pack, or mutation surface.
/// </summary>
public sealed class RuntimeCapabilityCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ReleaseCapabilityManifest _manifest;
    private readonly string _runtimeArtifactName;
    private readonly IReadOnlyDictionary<CapabilityKey, RuntimeCapabilityRegistration> _registrations;
    private readonly IReadOnlyDictionary<CapabilityKey, ICapabilityReadinessProbe> _probes;
    private readonly TimeSpan _probeTimeout;

    internal RuntimeCapabilityCatalog(
        VerifiedReleaseCapabilityManifest verifiedManifest,
        IEnumerable<RuntimeCapabilityRegistration> registrations,
        IEnumerable<ICapabilityReadinessProbe> probes,
        TimeSpan? probeTimeout = null)
        : this(verifiedManifest, "carrier.bundle", registrations, probes, probeTimeout)
    {
    }

    /// <summary>Constructs the immutable installation-scoped catalog.</summary>
    public RuntimeCapabilityCatalog(
        VerifiedReleaseCapabilityManifest verifiedManifest,
        string runtimeArtifactName,
        IEnumerable<RuntimeCapabilityRegistration> registrations,
        IEnumerable<ICapabilityReadinessProbe> probes,
        TimeSpan? probeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedManifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeArtifactName);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(probes);
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5);
        if (_probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        _manifest = verifiedManifest.Manifest;
        _runtimeArtifactName = runtimeArtifactName;
        ReleaseCapabilityManifestValidator.Validate(_manifest);
        if (!_manifest.Artifacts.Any(x => string.Equals(x.Name, _runtimeArtifactName, StringComparison.Ordinal)))
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                $"Runtime artifact '{_runtimeArtifactName}' is absent from the verified release manifest.");
        }
        if (!verifiedManifest.VerifiedArtifactNames.Contains(_runtimeArtifactName))
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                $"Runtime artifact '{_runtimeArtifactName}' bytes were not verified.");
        }
        foreach (var capability in _manifest.Capabilities.Where(entry => entry.ExecutableInventories.Any(
                     inventory => string.Equals(
                         inventory.ArtifactName,
                         _runtimeArtifactName,
                         StringComparison.Ordinal))))
        {
            var missing = capability.ExecutableInventories
                .Select(x => x.ArtifactName)
                .Where(x => !verifiedManifest.VerifiedArtifactNames.Contains(x))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length != 0)
            {
                throw new CapabilityCatalogAdmissionException(
                    CapabilityRefusalCodes.ManifestMismatch,
                    $"Capability '{capability.Key}' cannot be compiled until every required artifact " +
                    $"is verified; missing: {string.Join(", ", missing)}.");
            }
        }
        _registrations = IndexUnique(registrations, x => x.Key, "runtime registration");
        _probes = IndexUnique(probes, x => x.Key, "readiness probe");

        foreach (var registration in _registrations.Values)
        {
            ReleaseCapabilityManifestValidator.ValidateKey(registration.Key);
            if (!string.Equals(registration.ArtifactName, _runtimeArtifactName, StringComparison.Ordinal))
            {
                throw new CapabilityCatalogAdmissionException(
                    CapabilityRefusalCodes.ManifestMismatch,
                    $"Runtime registration '{registration.Key}' belongs to artifact " +
                    $"'{registration.ArtifactName}', not '{_runtimeArtifactName}'.");
            }
            ArgumentNullException.ThrowIfNull(registration.Executables);
            ReleaseCapabilityManifestValidator.ValidateInventory(registration.Executables);
        }
        foreach (var probe in _probes.Values)
        {
            ReleaseCapabilityManifestValidator.ValidateKey(probe.Key);
        }

        var compiled = ProjectedEntries().Select(x => x.Entry.Key).ToHashSet();
        RefuseExtras(_registrations.Keys, compiled, "runtime registration");
        RefuseExtras(_probes.Keys, compiled, "readiness probe");
    }

    /// <summary>Builds a deterministic snapshot. Probe exceptions fail only their capability closed.</summary>
    public async ValueTask<RuntimeCapabilityCatalogSnapshot> SnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var projected = ProjectedEntries().OrderBy(x => x.Entry.Key).ToArray();
        var states = new List<RuntimeCapabilityState>(projected.Length);
        foreach (var (entry, expectedInventory) in projected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_registrations.TryGetValue(entry.Key, out var registration))
            {
                states.Add(Unavailable(entry.Key, CapabilityRefusalCodes.NotAdmitted));
                continue;
            }

            if (!InventoryMatches(expectedInventory.Executables, registration.Executables))
            {
                states.Add(Unavailable(entry.Key, CapabilityRefusalCodes.ManifestMismatch));
                continue;
            }

            if (!_probes.TryGetValue(entry.Key, out var probe))
            {
                states.Add(new RuntimeCapabilityState(
                    entry.Key, Compiled: true, Admitted: true, Ready: false,
                    Array.AsReadOnly(new[] { CapabilityRefusalCodes.NotReady })));
                continue;
            }

            CapabilityReadinessResult readiness;
            try
            {
                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCancellation.CancelAfter(_probeTimeout);
                readiness = await probe.ProbeAsync(probeCancellation.Token).AsTask()
                    .WaitAsync(_probeTimeout, cancellationToken).ConfigureAwait(false)
                    ?? CapabilityReadinessResult.Unavailable();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                readiness = CapabilityReadinessResult.Unavailable();
            }
            catch (OperationCanceledException)
            {
                readiness = CapabilityReadinessResult.Unavailable();
            }
            catch
            {
                readiness = CapabilityReadinessResult.Unavailable();
            }

            states.Add(new RuntimeCapabilityState(
                entry.Key,
                Compiled: true,
                Admitted: true,
                Ready: readiness.Ready,
                Array.AsReadOnly(readiness.RefusalCodes.Order(StringComparer.Ordinal).ToArray())));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var catalogVersion = ComputeCatalogVersion(_manifest, _runtimeArtifactName, _registrations.Values);
        return new RuntimeCapabilityCatalogSnapshot(
            _manifest.ReleaseIdentity,
            _manifest.PlatformProfile,
            catalogVersion,
            ComputeSnapshotVersion(catalogVersion, states),
            Array.AsReadOnly(states.ToArray()));
    }

    private static RuntimeCapabilityState Unavailable(CapabilityKey key, string code) =>
        new(key, Compiled: true, Admitted: false, Ready: false, Array.AsReadOnly(new[] { code }));

    private IEnumerable<(ReleaseCapabilityEntry Entry, ArtifactExecutableInventory Inventory)> ProjectedEntries() =>
        _manifest.Capabilities.SelectMany(entry => entry.ExecutableInventories
            .Where(x => string.Equals(x.ArtifactName, _runtimeArtifactName, StringComparison.Ordinal))
            .Select(x => (entry, x)));

    private static IReadOnlyDictionary<CapabilityKey, T> IndexUnique<T>(
        IEnumerable<T> values,
        Func<T, CapabilityKey> keySelector,
        string kind)
    {
        var result = new Dictionary<CapabilityKey, T>();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                throw new CapabilityCatalogAdmissionException(
                    CapabilityRefusalCodes.ManifestMismatch,
                    $"Duplicate {kind} '{key}' is not allowed.");
            }
        }

        return result;
    }

    private static void RefuseExtras(
        IEnumerable<CapabilityKey> actual,
        IReadOnlySet<CapabilityKey> compiled,
        string kind)
    {
        var extra = actual.Where(x => !compiled.Contains(x)).Order().FirstOrDefault();
        if (extra != default)
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                $"The {kind} '{extra}' is absent from the verified release manifest.");
        }
    }

    private static bool InventoryMatches(ExecutableInventory expected, ExecutableInventory actual) =>
        CanonicalInventory(expected).SequenceEqual(CanonicalInventory(actual), StringComparer.Ordinal);

    private static string[] CanonicalInventory(ExecutableInventory inventory) =>
        inventory.Flatten()
            .Select(x => $"{x.Family}:{x.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ComputeCatalogVersion(
        ReleaseCapabilityManifest manifest,
        string runtimeArtifactName,
        IEnumerable<RuntimeCapabilityRegistration> registrations)
    {
        var canonical = new StringBuilder();
        AppendTokens(
            canonical,
            [
                manifest.SchemaVersion,
                manifest.Product,
                manifest.Channel,
                manifest.ReleaseIdentity,
                manifest.ReleaseSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                manifest.ReleasedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                manifest.PlatformProfile,
                manifest.SignerScope,
                manifest.Notes,
                runtimeArtifactName,
            ]);
        canonical.Append('\n');
        AppendTokens(canonical, ["artifact-count", manifest.Artifacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        canonical.Append('\n');
        foreach (var artifact in manifest.Artifacts.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            AppendField(
                canonical,
                "artifact",
                [
                    artifact.Name,
                    artifact.Platform,
                    artifact.Url,
                    artifact.Sha256,
                    artifact.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ]);
            canonical.Append('\n');
        }
        AppendTokens(canonical, ["capability-count", manifest.Capabilities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        canonical.Append('\n');
        foreach (var entry in manifest.Capabilities.OrderBy(x => x.Key))
        {
            AppendTokens(canonical, ["manifest", entry.Key.ToString()]);
            AppendTokens(
                canonical,
                [
                    "artifact-inventory-count",
                    entry.ExecutableInventories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ]);
            canonical.Append('\n');
            foreach (var inventory in entry.ExecutableInventories.OrderBy(x => x.ArtifactName, StringComparer.Ordinal))
            {
                AppendTokens(canonical, ["inventory-artifact", inventory.ArtifactName]);
                AppendField(canonical, "executables", CanonicalInventory(inventory.Executables));
                canonical.Append('\n');
            }
            AppendField(canonical, "recovery-actions", entry.RecoveryActions.Order(StringComparer.Ordinal));
            AppendTokens(
                canonical,
                ["durable-fact-count", entry.DurableFacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            canonical.Append('\n');
            foreach (var fact in entry.DurableFacts.OrderBy(x => x.FactType, StringComparer.Ordinal))
            {
                AppendField(
                    canonical,
                    "durable-fact",
                    [fact.FactType, fact.ArtifactName, fact.CurrentSchemaVersion]);
                AppendField(
                    canonical,
                    "readable-schema-versions",
                    fact.ReadableSchemaVersions.Order(StringComparer.Ordinal));
                AppendField(
                    canonical,
                    "recovery-handler-aliases",
                    fact.RecoveryHandlerAliases.Order(StringComparer.Ordinal));
                canonical.Append('\n');
            }
        }
        var runtimeRegistrations = registrations.OrderBy(x => x.Key).ToArray();
        AppendTokens(
            canonical,
            ["runtime-registration-count", runtimeRegistrations.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        canonical.Append('\n');
        foreach (var registration in runtimeRegistrations)
        {
            AppendTokens(canonical, ["runtime", registration.Key.ToString(), registration.ArtifactName]);
            AppendField(canonical, "executables", CanonicalInventory(registration.Executables));
            canonical.Append('\n');
        }

        return Hash(canonical);
    }

    private static string ComputeSnapshotVersion(
        string catalogVersion,
        IEnumerable<RuntimeCapabilityState> states)
    {
        var canonical = new StringBuilder();
        AppendTokens(canonical, [catalogVersion]);
        canonical.Append('\n');
        foreach (var state in states.OrderBy(x => x.Key))
        {
            AppendTokens(
                canonical,
                [
                    state.Key.ToString(),
                    state.Compiled ? "1" : "0",
                    state.Admitted ? "1" : "0",
                    state.Ready ? "1" : "0",
                ]);
            AppendField(canonical, "refusal-codes", state.RefusalCodes.Order(StringComparer.Ordinal));
            canonical.Append('\n');
        }

        return Hash(canonical);
    }

    private static string Hash(StringBuilder canonical)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(canonical.ToString())));
        }
        catch (EncoderFallbackException ex)
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                "Capability catalog text contains invalid Unicode and cannot be admitted.",
                ex);
        }
    }

    private static void AppendTokens(StringBuilder builder, IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            builder.Append(token.Length).Append(':').Append(token);
        }
    }

    private static void AppendField(StringBuilder builder, string name, IEnumerable<string> tokens)
    {
        var snapshot = tokens.ToArray();
        AppendTokens(
            builder,
            [name, snapshot.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        AppendTokens(builder, snapshot);
    }
}
