using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Temporal;

namespace Harborline.Api.Foundation.Assets.Hierarchy;

/// <summary>A temporal edge between two entities in the asset graph.</summary>
public sealed record EntityEdge(
    long Id,
    EntityId From,
    EntityId To,
    EdgeKind Kind,
    TemporalRange Validity,
    JsonDocument? Metadata);
