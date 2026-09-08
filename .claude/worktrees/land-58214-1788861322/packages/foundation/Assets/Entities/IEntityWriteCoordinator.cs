using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>Host-supplied admitted entry for generic entity mutations.</summary>
public interface IEntityWriteCoordinator
{
    ValueTask AuthorizeAsync(
        string recordId,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<EntityId> CreateAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask DeleteAsync(
        EntityId id,
        DeleteOptions options,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct = default);
}
