using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Blocks.LayoutRuntime;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>
/// T-735, owner ruling of 2026-11-04: where a related block's target record is looked up. The relationship map
/// (built from the Records reference fields the root's Record Type declares) names the target; this source only
/// answers whether that record exists and what its values are. It is the seam a Records instance store plugs into
/// (T-616 writes, T-617 reads, T-619 brings them into the API).
/// </summary>
/// <remarks>
/// <para>
/// The lookup is synchronous because <see cref="LayoutSurfaceHost"/> calls it through its traversal callbacks,
/// which the platform resolver runs synchronously on every discovery pass, inside the response floor. Never call it
/// before the host runs: a pre-load would make a denied target (it exists, so it is loaded) slower than a missing
/// one, outside the floor.
/// </para>
/// <para>
/// It is not an authorization point. The host decides <c>records:read</c> on the target after the lookup, and a
/// missing and a denied target then look the same to the caller (DES-0052 layout-eng-31).
/// </para>
/// </remarks>
public interface ILayoutRelatedRecordSource
{
    /// <summary>
    /// The values of <paramref name="target"/> in <paramref name="tenant"/>, or <see langword="null"/> when the
    /// record does not exist there.
    /// </summary>
    IReadOnlyDictionary<string, JsonNode?>? Find(TenantId tenant, LayoutRecordReference target);
}

/// <summary>
/// The production source until the host serves a Records instance store: every related target is absent, for
/// every caller and every key, so a related block resolves empty and the gate is never asked about a target.
/// </summary>
public sealed class AbsentLayoutRelatedRecordSource : ILayoutRelatedRecordSource
{
    /// <summary>The one instance.</summary>
    public static AbsentLayoutRelatedRecordSource Instance { get; } = new();

    private AbsentLayoutRelatedRecordSource()
    {
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonNode?>? Find(TenantId tenant, LayoutRecordReference target) => null;
}

/// <summary>
/// One request's view of an <see cref="ILayoutRelatedRecordSource"/>: each target is looked up once, however many
/// discovery passes the host runs, so every pass sees the same answer and the store is not read again per pass.
/// Not thread-safe; it lives for one resolution.
/// </summary>
internal sealed class LayoutRelatedRecordRequestCache(ILayoutRelatedRecordSource source, TenantId tenant)
{
    private readonly Dictionary<LayoutRecordReference, IReadOnlyDictionary<string, JsonNode?>?> _seen = [];

    /// <summary>The values of <paramref name="target"/>, read from the source on first use only.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Find(LayoutRecordReference target)
    {
        if (!_seen.TryGetValue(target, out var values))
        {
            values = source.Find(tenant, target);
            _seen[target] = values;
        }
        return values;
    }
}
