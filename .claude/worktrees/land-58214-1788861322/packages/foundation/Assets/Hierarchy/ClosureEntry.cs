using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Temporal;

namespace Harborline.Api.Foundation.Assets.Hierarchy;

/// <summary>
/// A single materialized entry in the hierarchy closure table.
/// </summary>
/// <remarks>
/// Plan D-HIERARCHY. <c>Depth = 0</c> denotes the self-entry; <c>Depth = 1</c> denotes a
/// direct parent relationship, etc.
/// </remarks>
public sealed record ClosureEntry(
    EntityId Ancestor,
    EntityId Descendant,
    int Depth,
    TemporalRange Validity);
