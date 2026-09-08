using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Registry;

/// <summary>The version policy a caller declares when resolving a named rule (ADR 0146 D5).</summary>
public enum RuleVersionPolicyKind
{
    /// <summary>The highest published version wins (default for forms/views/notifications).</summary>
    Latest = 0,

    /// <summary>An exact version (compliance scenarios; any pin change re-runs admission).</summary>
    Pinned = 1,

    /// <summary>A draft version — sandbox evaluation ONLY (never returned on a production resolve).</summary>
    Draft = 2,
}

/// <summary>
/// A caller-declared version policy (ADR 0146 D5): <c>latest</c> | <c>pinned:&lt;v&gt;</c> |
/// <c>draft</c>. Value type — cheap to pass per resolve.
/// </summary>
public readonly record struct RuleVersionPolicy
{
    private RuleVersionPolicy(RuleVersionPolicyKind kind, string? version)
    {
        Kind = kind;
        Version = version;
    }

    /// <summary>Which policy this is.</summary>
    public RuleVersionPolicyKind Kind { get; }

    /// <summary>The pinned version — non-null iff <see cref="Kind"/> is <see cref="RuleVersionPolicyKind.Pinned"/>.</summary>
    public string? Version { get; }

    /// <summary>Resolve the highest published (non-draft) version.</summary>
    public static RuleVersionPolicy Latest { get; } = new(RuleVersionPolicyKind.Latest, null);

    /// <summary>Resolve exactly <paramref name="version"/> (re-runs admission on any pin change).</summary>
    public static RuleVersionPolicy Pinned(string version)
        => new(RuleVersionPolicyKind.Pinned, version ?? throw new ArgumentNullException(nameof(version)));

    /// <summary>Resolve the latest draft — reachable ONLY through a sandbox-scoped resolve.</summary>
    public static RuleVersionPolicy Draft { get; } = new(RuleVersionPolicyKind.Draft, null);
}

/// <summary>
/// The trust boundary a resolve runs under (ADR 0146 D5 / board F6). The draft-exclusion is a
/// MECHANISM, not prose: a <see cref="Production"/> resolve NEVER returns a draft version — drafts
/// are reachable only through an explicit <see cref="Sandbox"/> resolve.
/// </summary>
public enum RuleResolveScope
{
    /// <summary>The production evaluation path — refuses every draft version (board F6).</summary>
    Production = 0,

    /// <summary>An explicitly sandbox-scoped resolve — the only path that reaches a draft.</summary>
    Sandbox = 1,
}

/// <summary>The outcome of a resolve.</summary>
public enum RuleResolutionStatus
{
    /// <summary>A version was resolved — <see cref="RuleResolution.Version"/> + <see cref="RuleResolution.Definition"/> are populated.</summary>
    Resolved = 0,

    /// <summary>No published version matched the (tenant, key, policy).</summary>
    NotFound = 1,

    /// <summary>A draft was requested (or the pinned version is a draft) on the production path — refused (board F6).</summary>
    DraftRefused = 2,
}

/// <summary>The result of resolving a named rule to a version + its definition.</summary>
public sealed record RuleResolution
{
    private RuleResolution(RuleResolutionStatus status, string? version, RuleDefinition? definition)
    {
        Status = status;
        Version = version;
        Definition = definition;
    }

    /// <summary>Whether a version resolved, was not found, or was a draft refused on the production path.</summary>
    public RuleResolutionStatus Status { get; }

    /// <summary>The resolved (or refused) version, when known.</summary>
    public string? Version { get; }

    /// <summary>The resolved definition — non-null iff <see cref="Status"/> is <see cref="RuleResolutionStatus.Resolved"/>.</summary>
    public RuleDefinition? Definition { get; }

    /// <summary>Convenience: true iff a version resolved.</summary>
    public bool IsResolved => Status == RuleResolutionStatus.Resolved;

    /// <summary>A resolved version.</summary>
    public static RuleResolution Resolved(string version, RuleDefinition definition)
        => new(RuleResolutionStatus.Resolved, version, definition);

    /// <summary>No match.</summary>
    public static RuleResolution NotFound { get; } = new(RuleResolutionStatus.NotFound, null, null);

    /// <summary>A draft refused on the production path (<paramref name="version"/> is the draft, or null for a draft-policy request).</summary>
    public static RuleResolution DraftRefused(string? version)
        => new(RuleResolutionStatus.DraftRefused, version, null);
}

/// <summary>
/// A D7 instance pin (ADR 0146 D5): the exact rule version captured at consumer-instance creation
/// (a running workflow, an in-progress report run, a document render). Resolving through the pin
/// replays deterministically against that version; re-admission happens when the consumer re-pins.
/// </summary>
public sealed record RulePin(string Tenant, string RuleKey, string Version);

/// <summary>One published rule version in the registry.</summary>
/// <param name="RuleKey">The named-rule key.</param>
/// <param name="Version">The monotonic version (S-8 watermark order, see <see cref="RuleVersion"/>).</param>
/// <param name="IsDraft">True for a draft — reachable only through a sandbox-scoped resolve.</param>
/// <param name="Definition">The rule definition compiled by the evaluator.</param>
public sealed record PublishedRuleVersion(string RuleKey, string Version, bool IsDraft, RuleDefinition Definition);

/// <summary>
/// The named-rule registry (ADR 0146 D5 / Wave 1): resolve a rule by <c>(tenant, rule-key)</c> under
/// a version policy. Composes shipped patterns, no new primitive — the S-8 monotonic watermark
/// (<see cref="RuleVersion"/>) for offline "latest", D7 instance pins (<see cref="RulePin"/>), and
/// (in the sibling <see cref="RuleCompileCache"/>) the HomeEpochFence read-tip-reject-stale
/// discipline for the per-node compile/cache swap.
/// </summary>
public interface IRuleRegistry
{
    /// <summary>Publishes (idempotent upsert) a rule version. A CRDT-synced record may arrive in any
    /// order — "latest" is resolved by the S-8 watermark, so a late older record simply never wins.</summary>
    void Publish(string tenant, PublishedRuleVersion published);

    /// <summary>Resolves a named rule under <paramref name="policy"/>. A <paramref name="scope"/> of
    /// <see cref="RuleResolveScope.Production"/> NEVER returns a draft (board F6).</summary>
    RuleResolution Resolve(string tenant, string ruleKey, RuleVersionPolicy policy, RuleResolveScope scope);

    /// <summary>Captures a D7 instance pin by resolving <paramref name="policy"/> once and freezing the
    /// version; returns null if nothing resolved (a refused/absent rule cannot be pinned).</summary>
    RulePin? Pin(string tenant, string ruleKey, RuleVersionPolicy policy, RuleResolveScope scope);

    /// <summary>Replays a D7 pin — resolves the exact pinned version (deterministic replay).</summary>
    RuleResolution ResolvePinned(RulePin pin, RuleResolveScope scope);
}

/// <summary>
/// In-memory <see cref="IRuleRegistry"/> — the Wave-1 unification floor. Holds published versions
/// per <c>(tenant, rule-key)</c> and resolves under the D5 version policies. Exercised in isolation:
/// no production CRDT sync feeds it yet — that is the Wave-2 cascade
/// (<c>PackContentKind.RuleDefinition</c>, D8). Thread-safe (a single gate over the store).
/// </summary>
public sealed class RuleRegistry : IRuleRegistry
{
    private readonly object _gate = new();
    private readonly IRestrictingDefinitionKindValidator _restrictingKinds;
    // (tenant, ruleKey) -> version -> published. Inner dict keyed by version for idempotent upsert.
    private readonly Dictionary<(string Tenant, string Key), Dictionary<string, PublishedRuleVersion>> _store = new();

    /// <summary>Constructs the registry over the canonical restricting-kind validator.</summary>
    public RuleRegistry(IRestrictingDefinitionKindValidator? restrictingKinds = null)
        => _restrictingKinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;

    /// <inheritdoc />
    public void Publish(string tenant, PublishedRuleVersion published)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(published);
        _restrictingKinds.EnsureKnown(
            RestrictingDefinitionKindFamily.RuleAction,
            published.RuleKey,
            published.Definition.Action.ToString(),
            nestedDefinitionId: published.Definition.Id);
        lock (_gate)
        {
            var key = (tenant, published.RuleKey);
            if (!_store.TryGetValue(key, out var versions))
            {
                _store[key] = versions = new Dictionary<string, PublishedRuleVersion>(StringComparer.Ordinal);
            }
            versions[published.Version] = published; // idempotent upsert (CRDT record)
        }
    }

    /// <inheritdoc />
    public RuleResolution Resolve(string tenant, string ruleKey, RuleVersionPolicy policy, RuleResolveScope scope)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(ruleKey);
        lock (_gate)
        {
            if (!_store.TryGetValue((tenant, ruleKey), out var versions) || versions.Count == 0)
            {
                return RuleResolution.NotFound;
            }

            switch (policy.Kind)
            {
                case RuleVersionPolicyKind.Pinned:
                {
                    if (policy.Version is null || !versions.TryGetValue(policy.Version, out var pinned))
                    {
                        return RuleResolution.NotFound;
                    }
                    // A pinned DRAFT is refused on the production path — the draft-exclusion mechanism.
                    if (pinned.IsDraft && scope == RuleResolveScope.Production)
                    {
                        return RuleResolution.DraftRefused(pinned.Version);
                    }
                    return RuleResolution.Resolved(pinned.Version, pinned.Definition);
                }

                case RuleVersionPolicyKind.Draft:
                {
                    // The draft policy is reachable ONLY through a sandbox-scoped resolve (board F6).
                    if (scope == RuleResolveScope.Production)
                    {
                        return RuleResolution.DraftRefused(null);
                    }
                    return Highest(versions.Values.Where(v => v.IsDraft));
                }

                default: // Latest
                {
                    // Production excludes drafts (the mechanism); sandbox sees every version.
                    var pool = scope == RuleResolveScope.Production
                        ? versions.Values.Where(v => !v.IsDraft)
                        : versions.Values;
                    return Highest(pool);
                }
            }
        }
    }

    /// <inheritdoc />
    public RulePin? Pin(string tenant, string ruleKey, RuleVersionPolicy policy, RuleResolveScope scope)
    {
        var resolution = Resolve(tenant, ruleKey, policy, scope);
        return resolution.IsResolved ? new RulePin(tenant, ruleKey, resolution.Version!) : null;
    }

    /// <inheritdoc />
    public RuleResolution ResolvePinned(RulePin pin, RuleResolveScope scope)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return Resolve(pin.Tenant, pin.RuleKey, RuleVersionPolicy.Pinned(pin.Version), scope);
    }

    // Picks the highest version in the pool by the S-8 monotonic watermark — order-independent, so
    // two offline peers holding the same set converge on the identical "latest".
    private static RuleResolution Highest(IEnumerable<PublishedRuleVersion> pool)
    {
        PublishedRuleVersion? best = null;
        foreach (var candidate in pool)
        {
            if (best is null || RuleVersion.Compare(candidate.Version, best.Version) > 0)
            {
                best = candidate;
            }
        }
        return best is null ? RuleResolution.NotFound : RuleResolution.Resolved(best.Version, best.Definition);
    }
}
