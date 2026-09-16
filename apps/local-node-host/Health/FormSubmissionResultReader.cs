using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Reads a committed projection result; it never derives an optimistic result from input.</summary>
public interface IFormSubmissionResultReader
{
    ValueTask<JsonElement?> ReadResultAsync(FormDefinitionId form, TenantId tenant, EntityId instance,
        CancellationToken cancellationToken = default);
}

internal static class FormSubmissionAuditReceipt
{
    internal static async ValueTask<(Guid AuditId, Guid CorrelationId)?> ReadAsync(IAuditTrail trail,
        FormDefinitionId form, TenantId tenant, ActorId actor, FormSubmitReceipt receipt, CancellationToken ct)
    {
        await foreach (var row in trail.QueryAsync(new AuditQuery(tenant,
            new AuditEventType("Forms.InstanceMinted"), receipt.SubmittedAt, receipt.SubmittedAt), ct))
        {
            if (row.Actor != actor || row.Target?.RecordId != form.Value || row.AuthoritySnapshot is null ||
                !row.Payload.Payload.Body.TryGetValue("entity_id", out var entity)) continue;
            var id = entity switch { string text => text, JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(), _ => null };
            if (id == receipt.InstanceId.ToString()) return (row.AuditId, row.Payload.Nonce);
        }
        return null;
    }
}
