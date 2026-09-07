using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>Filter criteria for <see cref="IEntityStore.QueryAsync"/>.</summary>
/// <param name="Schema">Restrict to entities of this schema. Null matches any schema.</param>
/// <param name="Tenant">
/// Tenant scope. Null = system-scope (sentinels visible); use <see cref="TenantSelection.All"/>
/// for admin-scope (sentinels excluded).
/// </param>
/// <param name="AsOf">As-of instant for temporal reads. Null reads the current version.</param>
/// <param name="IncludeDeleted">When true, soft-deleted entities are included.</param>
/// <param name="Limit">Maximum number of entities to return. Null is unbounded.</param>
/// <param name="BodyContains">
/// Optional JSON-containment predicate over the entity body. When non-null, only entities whose
/// body contains this JSON fragment are returned, matching PostgreSQL's <c>@&gt;</c> jsonb containment
/// semantics (object: every key present and recursively contained; array: every element contained in
/// some target element; scalar: deep equality). The value is the raw JSON text of the fragment and is
/// always passed to the store as a bound parameter — never interpolated into SQL. Use
/// <see cref="WhereBodyContains(JsonDocument)"/> to build it from a <see cref="JsonDocument"/>.
/// <para>
/// <b>Interaction with <paramref name="AsOf"/>.</b> Containment filters the entity's <i>current</i>
/// body, not the body as it stood at the as-of instant: an as-of read returns the historical
/// version of each entity whose <i>current</i> body matches the fragment, not entities whose
/// as-of-version body matched. Pair <c>BodyContains</c> with <c>AsOf</c> only when "matches now"
/// is the intended selection semantics.
/// </para>
/// </param>
public sealed record EntityQuery(
    SchemaId? Schema = null,
    TenantSelection? Tenant = null,
    DateTimeOffset? AsOf = null,
    bool IncludeDeleted = false,
    int? Limit = null,
    string? BodyContains = null)
{
    /// <summary>
    /// Returns a copy of this query with <see cref="BodyContains"/> set to the raw JSON text of
    /// <paramref name="fragment"/>. The text is captured eagerly so the caller may dispose the
    /// document immediately.
    /// </summary>
    public EntityQuery WhereBodyContains(JsonDocument fragment) =>
        this with { BodyContains = fragment.RootElement.GetRawText() };
}
