using Harborline.Api.Foundation.RuleEngine.Compilation;

namespace Harborline.Api.Foundation.RuleEngine.Registry;

/// <summary>
/// Thrown when a compile/cache swap would install a STALE (downgrade) compiled AST over a newer one
/// already cached for the same key — the reject-stale half of the S-8 watermark (ADR 0146 D5).
/// </summary>
public sealed class StaleRulePublishException : Exception
{
    /// <summary>Constructs the exception with the cache key + the stale and current versions.</summary>
    public StaleRulePublishException(string cacheKey, string staleVersion, string currentVersion)
        : base($"rule compile cache '{cacheKey}': refused a stale publish (version '{staleVersion}' is below the cached tip '{currentVersion}')")
    {
        CacheKey = cacheKey;
        StaleVersion = staleVersion;
        CurrentVersion = currentVersion;
    }

    /// <summary>The <c>(tenant, rule-key)</c> cache key.</summary>
    public string CacheKey { get; }

    /// <summary>The rejected (stale) version.</summary>
    public string StaleVersion { get; }

    /// <summary>The cached tip version that stands.</summary>
    public string CurrentVersion { get; }
}

/// <summary>
/// The per-node compiled-AST cache with the HomeEpochFence read-tip-reject-stale discipline
/// (ADR 0146 D5). A compile/cache swap for a rule key is MONOTONIC: reading the currently cached tip
/// version under the SAME critical section as the swap, a publish whose version is a downgrade of
/// the cached tip is REJECTED — so a stale publish arriving late can never overwrite a newer,
/// already-compiled AST mid-transaction. This mirrors the shipped <c>HomeEpochFence</c>
/// (read-the-tip-on-this-transaction, reject-if-stale) rather than inventing a new primitive (D5).
///
/// <para>In-memory here; the read-compare-swap is made atomic by a lock — the in-process analog of
/// the fence's <c>BEGIN IMMEDIATE</c> write lock, closing the read-through-write TOCTOU the fence
/// exists to close (the same reason <c>HomeEpochFence</c> requires the caller to hold the write lock
/// before the tip read). Cached in isolation for Wave 1: no production node yet drives the swap from
/// a live sync feed (Wave-2 cascade, D8).</para>
/// </summary>
public sealed class RuleCompileCache
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Key), (string Version, CompiledGraph Graph)> _cache = new();

    /// <summary>Reads the cached compiled AST for <c>(tenant, ruleKey)</c>, if any.</summary>
    public bool TryGet(string tenant, string ruleKey, out string version, out CompiledGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(ruleKey);
        lock (_gate)
        {
            if (_cache.TryGetValue((tenant, ruleKey), out var cur))
            {
                version = cur.Version;
                graph = cur.Graph;
                return true;
            }
            version = string.Empty;
            graph = null;
            return false;
        }
    }

    /// <summary>
    /// Installs a freshly-compiled AST for <paramref name="version"/> into the cache, MONOTONICALLY.
    /// The tip read + version compare + swap run inside one lock (atomic — no TOCTOU). A version that
    /// is a downgrade of the cached tip is a stale publish and is REFUSED
    /// (<see cref="StaleRulePublishException"/>); the newer cached AST stands. An equal or higher
    /// version installs (equal re-installs are idempotent). Returns the now-cached graph.
    /// </summary>
    public CompiledGraph Swap(string tenant, string ruleKey, string version, CompiledGraph graph)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(ruleKey);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(graph);
        lock (_gate)
        {
            var key = (tenant, ruleKey);
            if (_cache.TryGetValue(key, out var cur) && RuleVersion.IsDowngrade(cur.Version, version))
            {
                throw new StaleRulePublishException($"{tenant}/{ruleKey}", version, cur.Version);
            }
            _cache[key] = (version, graph);
            return graph;
        }
    }
}
