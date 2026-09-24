using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>A durable commit whose acknowledgement was lost. The pointer may or may not have moved.</summary>
/// <remarks>
/// T-644: never a refusal and never a retry. The evidence intent identity is the recovery handle; a re-request
/// carrying the same intent and inputs is answered from the committed outbox, and the kernel (T-587) brings a
/// crashed pointer or outbox to a terminal state.
/// </remarks>
public sealed class ConfigurationCommitIndeterminateException(string evidenceIntentId, Exception inner)
    : Exception("configuration-commit-indeterminate", inner)
{
    /// <summary>The tenant-scoped evidence intent the commit was bound to.</summary>
    public string EvidenceIntentId { get; } = evidenceIntentId;
}

/// <summary>A prepared candidate read back from the isolated projection artifact, or the host's refusal.</summary>
public sealed record HostConfigurationPreparation(ConfigurationPreparation? Preparation,
    ConfigurationGeneration Baseline, ConfigurationActivationRefusal? HostRefusal);

/// <summary>An activation already acknowledged for an evidence intent: the committed generation digests.</summary>
public sealed record ConfigurationAcknowledgedIntent(string PriorDigest, string NewDigest, bool InputsMatch);

/// <summary>
/// The api half of atomic activation (T-644, DES-0044 governance-ck-7): the host transaction behind the
/// platform's <see cref="IConfigurationActivationTarget"/>. One serializable SQLite transaction under the pack
/// projection write lease reads the current generation, verifies the prepared projection and destination,
/// takes the platform's pure compare-and-swap decision with live Access authority, and commits ownership
/// selections, the effective pointer, the Access decision reference and the evidence intent together.
/// </summary>
/// <remarks>
/// The bar this slice draws: a candidate is resolved over packs that are already installed and Active at the
/// destination, so what the switch makes effective is the generation identity and its ownership selections.
/// Per-pack lifecycle flips stay on the installer's own transaction. Recovery is not here (T-587).
/// </remarks>
public sealed class ConfigurationActivationTarget : IPackProjectionParticipant
{
    internal const string ProjectionRevision = "1";
    internal const string PlatformPackKey = "harborline.platform";
    private static readonly AuthorizationOperation Operate = AuthorizationOperation.Parse(Permission.PackagesOperate);

    private readonly IDbContextFactory<NodeLocalPacksDbContext> _factory;
    private readonly DurablePackInstallStore _packs;
    private readonly AuthorizationGate _gate;
    private readonly IPackInstallAudit _audit;
    private readonly IPackPlatformCompatibility? _platform;
    private PackProjectionSqliteUnit? _unit;

    /// <summary>Composes the target over the host's durable pack store and its Access gate.</summary>
    public ConfigurationActivationTarget(IDbContextFactory<NodeLocalPacksDbContext> factory, DurablePackInstallStore packs,
        AuthorizationGate gate, IPackInstallAudit audit, IPackPlatformCompatibility? platform = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _platform = platform;
    }

    /// <summary>Crash injection for the crash-path tests: named points throw or block; production leaves it null.</summary>
    internal Action<string>? CrashPoint { get; set; }

    /// <inheritdoc />
    public void StageProjection(PackProjectionTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.Stage(this, () =>
        {
            _unit = transaction.Durable(() => new PackProjectionSqliteUnit(_factory.CreateDbContext()));
            transaction.Finally(() => _unit = null);
            return static () => { };
        });
    }

    private NodeLocalPacksDbContext CreateContext()
    {
        var context = _factory.CreateDbContext();
        return _unit is null ? context : _unit.Join(context);
    }

    /// <summary>One pinned read of the effective generation: the pointer, or the host's resolution of Active packs.</summary>
    public ConfigurationGeneration ReadEffective(TenantId tenant)
    {
        using var lease = PackProjectionActivationBarrier.Read();
        using var context = CreateContext();
        return ReadCurrent(context, tenant);
    }

    /// <summary>The bound act: the platform interface over this request's server-derived authority and instant.</summary>
    public IConfigurationActivationTarget For(AuthorizationWriteContext authority) => new BoundAct(this, authority);

    /// <summary>Answers a re-request by evidence intent identity from the committed outbox, never by re-running the switch.</summary>
    public ConfigurationAcknowledgedIntent? Acknowledged(TenantId tenant, ConfigurationEvidenceIntent intent,
        string candidateDigest, string expectedBaselineDigest, string principal)
    {
        ArgumentNullException.ThrowIfNull(intent);
        using var lease = PackProjectionActivationBarrier.Read();
        using var context = CreateContext();
        var row = context.EvidenceOutbox.AsNoTracking().FirstOrDefault(r => r.Tenant == tenant.Value && r.IntentId == intent.Id);
        return row is null
            ? null
            : new(row.PriorDigest, row.NewDigest,
                row.InputsDigest == InputsDigest(candidateDigest, expectedBaselineDigest, principal, intent.Reason));
    }

    /// <summary>
    /// Prepares an isolated candidate over installed Active packs against an explicit expected baseline. May
    /// write the isolated projection artifact; never touches ownership, evidence or the pointer.
    /// </summary>
    public HostConfigurationPreparation Prepare(TenantId tenant, string expectedBaselineDigest,
        IReadOnlyList<string> activePackageKeys, IReadOnlyDictionary<string, string> ownership, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activePackageKeys);
        ArgumentNullException.ThrowIfNull(ownership);
        using var lease = PackProjectionActivationBarrier.Read();
        using var context = CreateContext();
        var baseline = ReadCurrent(context, tenant);
        ConfigurationGeneration candidate;
        try
        {
            candidate = ConfigurationGeneration.Resolve(ResolveDestination(tenant, activePackageKeys, ownership));
        }
        catch (ArgumentException exception)
        {
            return new(null, baseline, new(exception.Message, "candidate", "The candidate could not be resolved over the installed Active packs."));
        }
        var preparation = ConfigurationPreparation.Prepare(baseline, expectedBaselineDigest, candidate, (_, resolved) =>
        {
            var json = Canonical(resolved);
            var digest = Sha256(json);
            var existing = context.PreparedProjections.Find(tenant.Value, resolved.Digest);
            if (existing is null)
            {
                context.PreparedProjections.Add(new ConfigurationPreparedProjectionRow
                {
                    Tenant = tenant.Value, CandidateDigest = resolved.Digest, Revision = ProjectionRevision,
                    ProjectionDigest = digest, ReferencesJson = json, BaselineDigest = baseline.Digest,
                    DestinationDigest = DestinationDigest(tenant), PreparedAt = now,
                });
            }
            else
            {
                existing.BaselineDigest = baseline.Digest;
                existing.DestinationDigest = DestinationDigest(tenant);
                existing.PreparedAt = now;
            }
            context.SaveChanges();
            CrashPoint?.Invoke("prepared");
            return new ConfigurationProjectionValidation(new(resolved.Digest, ProjectionRevision, digest), []);
        });
        return new(preparation, baseline, null);
    }

    /// <summary>Re-reads a prepared candidate by digest through the producer, verifying the isolated artifact.</summary>
    public HostConfigurationPreparation Reprepare(TenantId tenant, string expectedBaselineDigest, string candidateDigest)
    {
        using var lease = PackProjectionActivationBarrier.Read();
        using var context = CreateContext();
        var baseline = ReadCurrent(context, tenant);
        var row = context.PreparedProjections.AsNoTracking().FirstOrDefault(r => r.Tenant == tenant.Value && r.CandidateDigest == candidateDigest);
        if (row is null)
            return new(null, baseline, new("configuration-projection-missing", "projection", "No prepared projection exists for the candidate."));
        var candidate = FromReferences(row.ReferencesJson);
        if (candidate.Digest != candidateDigest || Sha256(row.ReferencesJson) != row.ProjectionDigest)
            return new(null, baseline, new("configuration-projection-changed", "projection", "The prepared projection no longer derives the candidate it was prepared for."));
        return new(ConfigurationPreparation.Prepare(baseline, expectedBaselineDigest, candidate,
            (_, resolved) => Verify(context, tenant, resolved, new(resolved.Digest, row.Revision, row.ProjectionDigest))), baseline, null);
    }

    private async ValueTask<ConfigurationActivationOutcome> CompareAndSwapAsync(ConfigurationActivationRequest request,
        AuthorizationWriteContext authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prepared);
        ArgumentNullException.ThrowIfNull(request.EvidenceIntent);
        var tenant = authority.Tenant;
        var now = authority.At;
        var intent = request.EvidenceIntent;
        var inputs = InputsDigest(request.Prepared.Candidate.Digest, request.Prepared.Baseline.Digest, request.Principal, intent.Reason);
        ConfigurationActivationOutcome outcome;
        AuthorizationDecision? decision = null;
        using (var transaction = new PackProjectionTransaction(cancellationToken))
        {
            transaction.Enlist(_packs);
            transaction.Enlist(this);
            using var context = CreateContext();
            var current = ReadCurrent(context, tenant);

            // The evidence intent is scoped to the tenant; reuse with different inputs refuses, the same inputs
            // are already committed and are answered by Acknowledged, never by a second switch.
            var reused = context.EvidenceOutbox.AsNoTracking().FirstOrDefault(r => r.Tenant == tenant.Value && r.IntentId == intent.Id);
            if (reused is not null)
            {
                return ConfigurationActivationOutcome.Refused(current, request, new(
                    reused.InputsDigest == inputs ? "configuration-evidence-intent-acknowledged" : "configuration-evidence-intent-reused",
                    "evidenceIntent", "The evidence intent identity was already committed for this tenant."));
            }

            // Host obligations before the decision: the prepared projection is present and unchanged, and the
            // destination still matches what preparation validated.
            var verification = Verify(context, tenant, request.Prepared.Candidate, request.Prepared.Projection);
            if (verification.Findings.Count > 0)
                return ConfigurationActivationOutcome.Refused(current, request, verification.Findings[0]);

            // Point-of-use authority for THIS request, resolved live and bound to the decision by identity.
            var gateRequest = authority.InstallWide(Operate);
            decision = await _gate.DecideAsync(gateRequest, cancellationToken).ConfigureAwait(false);
            var decisionId = DecisionId(decision);
            var access = new ConfigurationActivationAuthority(decision.Verdict == AuthorizationVerdict.Allowed, decisionId);
            var decided = ConfigurationActivation.DecideCompareAndSwap(current, request,
                candidate => ReferenceEquals(candidate, request) ? access : new(false, string.Empty));
            if (decided.Refusal is not null) return ConfigurationActivationOutcome.Refused(decided);

            // Everything below commits together or not at all.
            foreach (var owner in request.Prepared.Ownership)
                _packs.RecordKeyOwnership(tenant, owner.DefinitionKey, owner.PackageKey);
            CrashPoint?.Invoke("ownership-written");
            var candidateJson = Canonical(request.Prepared.Candidate);
            // VSTHRD103: an async method awaits the async read rather than the blocking Find.
            var pointer = await context.EffectiveGenerations.FindAsync([tenant.Value], cancellationToken).ConfigureAwait(false);
            if (pointer is null)
            {
                context.EffectiveGenerations.Add(new ConfigurationEffectiveGenerationRow
                {
                    Tenant = tenant.Value, Digest = request.Prepared.Candidate.Digest, ReferencesJson = candidateJson,
                    Principal = request.Principal, ActivatedAt = now, DecisionId = decisionId, EvidenceIntentId = intent.Id,
                });
            }
            else
            {
                if (pointer.Digest != current.Digest) throw new InvalidOperationException("configuration-effective-moved");
                pointer.Digest = request.Prepared.Candidate.Digest;
                pointer.ReferencesJson = candidateJson;
                pointer.Principal = request.Principal;
                pointer.ActivatedAt = now;
                pointer.DecisionId = decisionId;
                pointer.EvidenceIntentId = intent.Id;
            }
            context.EvidenceOutbox.Add(new ConfigurationEvidenceOutboxRow
            {
                Tenant = tenant.Value, IntentId = intent.Id, Reason = intent.Reason, InputsDigest = inputs,
                DecisionId = decisionId, DecisionJson = DecisionJson(decision), PriorDigest = current.Digest,
                NewDigest = request.Prepared.Candidate.Digest, Principal = request.Principal, CommittedAt = now,
            });
            context.SaveChanges();
            CrashPoint?.Invoke("before-commit");
            try
            {
                transaction.Commit();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ConfigurationCommitIndeterminateException(intent.Id, exception);
            }
            try
            {
                CrashPoint?.Invoke("after-commit");
            }
            catch (Exception exception)
            {
                throw new ConfigurationCommitIndeterminateException(intent.Id, exception);
            }
            outcome = ConfigurationActivationOutcome.ConfirmCommitted(decided);
        }

        // Publication runs after the durable commit and outside the lease, with the decision that admitted the
        // switch. A publication failure leaves the row pending as the evidence that publication is owed; the
        // kernel's recovery (T-587) brings it to a terminal state, and nothing here turns the committed switch
        // into a refusal or republishes without its decision.
        try
        {
            CrashPoint?.Invoke("before-publish");
            using var context = CreateContext();
            var row = context.EvidenceOutbox.First(r => r.Tenant == tenant.Value && r.IntentId == intent.Id);
            _audit.AppendAuthorized(Entry(row, now), decision!);
            row.PublishedAt = now;
            context.SaveChanges();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Committed but unpublished: the outbox row is the evidence that publication is owed.
            _ = exception;
        }
        return outcome;
    }

    private ConfigurationProjectionValidation Verify(NodeLocalPacksDbContext context, TenantId tenant,
        ConfigurationGeneration candidate, ConfigurationReference projection)
    {
        var row = context.PreparedProjections.AsNoTracking().FirstOrDefault(r => r.Tenant == tenant.Value && r.CandidateDigest == candidate.Digest);
        ConfigurationProjectionValidation Finding(string code, string message) => new(projection, [new(code, "projection", message)]);
        if (row is null) return Finding("configuration-projection-missing", "The prepared projection no longer exists.");
        if (row.Revision != projection.Revision || Sha256(row.ReferencesJson) != projection.Digest
            || row.ProjectionDigest != projection.Digest || FromReferences(row.ReferencesJson).Digest != candidate.Digest)
            return Finding("configuration-projection-changed", "The prepared projection differs from the prepared reference.");
        if (row.DestinationDigest != DestinationDigest(tenant))
            return Finding("configuration-destination-incompatible", "The destination changed since the candidate was prepared.");
        return new(projection, []);
    }

    private ConfigurationGeneration ReadCurrent(NodeLocalPacksDbContext context, TenantId tenant)
    {
        var row = context.EffectiveGenerations.AsNoTracking().FirstOrDefault(r => r.Tenant == tenant.Value);
        if (row is null) return ConfigurationGeneration.Resolve(ResolveDestination(tenant, null, _packs.GetKeyOwnership(tenant)));
        var generation = FromReferences(row.ReferencesJson);
        // A pointer whose bytes no longer hash to its digest is corrupt: fail closed, name the state (T-587 repairs it).
        return generation.Digest == row.Digest ? generation : throw new InvalidOperationException("configuration-effective-corrupt");
    }

    /// <summary>The host's resolved inputs: Active packs, their content and declared dependencies, ownership, contract.</summary>
    internal ResolvedConfiguration ResolveDestination(TenantId tenant, IReadOnlyList<string>? roots, IReadOnlyDictionary<string, string> ownership)
    {
        var active = _packs.ListInstalled(tenant).Where(pack => pack.Lifecycle == PackLifecycleState.Active)
            .ToDictionary(pack => pack.PackKey, StringComparer.Ordinal);
        roots ??= active.Keys.Order(StringComparer.Ordinal).ToArray();
        var packages = new List<ConfigurationPackage>();
        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var key))
        {
            if (!seen.Add(key)) continue;
            if (!active.TryGetValue(key, out var pack)) throw new ArgumentException("configuration-package-not-active");
            var content = pack.SeedItems.Select(item => new ConfigurationReference(item.Key, item.Version, Sha256(item.CanonicalJson))).ToArray();
            foreach (var item in content)
            {
                if (!claims.TryGetValue(item.Key, out var claimants)) claims[item.Key] = claimants = [];
                claimants.Add(key);
            }
            var dependencies = pack.Dependencies.Select(dependency => dependency.Key).ToArray();
            packages.Add(new(new(key, pack.Version, PackageDigest(content)), content, dependencies));
            foreach (var dependency in dependencies) pending.Push(dependency);
        }
        var owners = claims.Select(claim =>
        {
            if (ownership.TryGetValue(claim.Key, out var chosen)) return new ConfigurationOwnership(claim.Key, chosen);
            if (claim.Value.Count == 1) return new ConfigurationOwnership(claim.Key, claim.Value[0]);
            throw new ArgumentException("configuration-ownership-required");
        }).ToArray();
        var contract = active.TryGetValue(PlatformPackKey, out var platform)
            ? new ConfigurationReference(PlatformPackKey, platform.Version, PackageDigest(platform.SeedItems.Select(item => new ConfigurationReference(item.Key, item.Version, Sha256(item.CanonicalJson))).ToArray()))
            : new ConfigurationReference(PlatformPackKey, _platform?.PlatformVersion ?? "0", Sha256(_platform?.PlatformVersion ?? "0"));
        return new(tenant.Value, roots, packages, owners, contract, []);
    }

    /// <summary>What preparation validated the candidate against: the Active pack set and the platform build.</summary>
    internal string DestinationDigest(TenantId tenant) => Sha256(string.Join("\n",
        _packs.ListInstalled(tenant).Where(pack => pack.Lifecycle == PackLifecycleState.Active)
            .Select(pack => pack.PackKey + "@" + pack.Version).Order(StringComparer.Ordinal)
            .Append("platform@" + (_platform?.PlatformVersion ?? "0"))));

    /// <summary>Round-trips the canonical reference document through the producer; the digest is re-derived, never read.</summary>
    internal static ConfigurationGeneration FromReferences(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static ConfigurationReference Reference(JsonElement element) => new(
            element.GetProperty("key").GetString()!, element.GetProperty("revision").GetString()!, element.GetProperty("digest").GetString()!);
        return ConfigurationGeneration.Resolve(new ResolvedConfiguration(
            root.GetProperty("tenantKey").GetString()!,
            root.GetProperty("activePackageKeys").EnumerateArray().Select(key => key.GetString()!).ToArray(),
            root.GetProperty("packages").EnumerateArray().Select(package => new ConfigurationPackage(
                Reference(package.GetProperty("reference")),
                package.GetProperty("content").EnumerateArray().Select(Reference).ToArray(),
                package.GetProperty("dependencies").EnumerateArray().Select(key => key.GetString()!).ToArray())).ToArray(),
            root.GetProperty("ownership").EnumerateArray().Select(owner => new ConfigurationOwnership(
                owner.GetProperty("definitionKey").GetString()!, owner.GetProperty("packageKey").GetString()!)).ToArray(),
            Reference(root.GetProperty("platformContract")),
            root.GetProperty("policies").EnumerateArray().Select(Reference).ToArray()));
    }

    internal static string Canonical(ConfigurationGeneration generation) => JsonSerializer.Serialize(generation.References);

    private static string PackageDigest(IReadOnlyList<ConfigurationReference> content) => Sha256(string.Join("\n",
        content.Select(item => item.Key + "\n" + item.Revision + "\n" + item.Digest).Order(StringComparer.Ordinal)));

    private static string InputsDigest(string candidateDigest, string baselineDigest, string principal, string reason) =>
        Sha256(string.Join("\n", candidateDigest, baselineDigest, principal, reason));

    private static string DecisionId(AuthorizationDecision decision) => Sha256(DecisionJson(decision));

    private static string DecisionJson(AuthorizationDecision decision) => JsonSerializer.Serialize(new
    {
        principal = decision.Request.Principal.Value,
        tenant = decision.Request.Tenant.Value,
        operation = decision.Request.Act.Operation.Value,
        decidedAt = decision.DecidedAt,
        verdict = decision.Verdict.ToString(),
    });

    private static PackInstallAuditEntry Entry(ConfigurationEvidenceOutboxRow row, DateTimeOffset now) => new(
        new TenantId(row.Tenant), PackInstallAuditAction.Activated, "configuration-generation", row.NewDigest, now, null, null,
        $"configuration.activated:{row.IntentId}:{row.PriorDigest}->{row.NewDigest}:{row.DecisionId}",
        ActingPrincipal: row.Principal);

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class BoundAct(ConfigurationActivationTarget target, AuthorizationWriteContext authority) : IConfigurationActivationTarget
    {
        public ValueTask<ConfigurationActivationOutcome> CompareAndSwapAsync(ConfigurationActivationRequest request,
            CancellationToken cancellationToken = default) => target.CompareAndSwapAsync(request, authority, cancellationToken);
    }
}
