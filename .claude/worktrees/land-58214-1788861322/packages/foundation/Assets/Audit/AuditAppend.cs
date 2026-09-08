using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Audit;

/// <summary>
/// Operational input to <see cref="IAuditLog.AppendAsync"/>. The log fills in
/// <see cref="AuditRecord.Id"/>, <see cref="AuditRecord.Prev"/>, and
/// <see cref="AuditRecord.Hash"/>.
/// </summary>
public sealed record AuditAppend(
    EntityId EntityId,
    VersionId? VersionId,
    Op Op,
    ActorId Actor,
    TenantId Tenant,
    DateTimeOffset At,
    JsonDocument Payload,
    string? Justification = null,
    byte[]? Signature = null);
