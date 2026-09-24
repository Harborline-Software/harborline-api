using System.Globalization;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Blocks.LayoutRuntime;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>
/// DES-0052 layout-eng-31 and layout-run-5, host half (T-731): Layout's protected related-binding
/// decision trace, stored in the authorization gate log T-498 names, which is the unified audit trail
/// <see cref="Health.AuthorizationRefusalAudit"/> writes refusals to. One instance serves one request.
/// </summary>
/// <remarks>
/// The record is appended inside <see cref="RecordDenial"/>, while the resolver is still running
/// (ADR 0068 decision 1: recorded at the time of the act, never reconstructed). An append fault is
/// logged as an error and not thrown, like <see cref="Health.AuthorizationRefusalAudit"/>: a fault only
/// the denied path can raise would tell the viewer a denied target from a missing one.
/// </remarks>
public sealed class LayoutDenialGateLog(
    IAuditTrail trail, IOperationSigner signer, TenantId tenant, TimeProvider time, ILogger logger) : ILayoutDecisionTrace
{
    /// <summary>The event type a related-binding denial is recorded under.</summary>
    public static readonly AuditEventType LayoutRelatedDeniedEventType = new("LayoutRelatedDenied");

    private static readonly AuthorizationOperation RecordsRead = AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead);

    /// <inheritdoc />
    public void RecordDenial(LayoutRelatedDenial denial)
    {
        ArgumentNullException.ThrowIfNull(denial);
        try
        {
            // ponytail: the platform trace is synchronous, so the append blocks the resolving thread.
            // The host has no synchronization context; an async trace needs a platform interface change.
            AppendAsync(denial).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Layout related-binding denial append FAILED (tenant {Tenant}, request {RequestId}, block {BlockId}); "
                + "the viewer still sees absence but the gate log has no record.",
                tenant, denial.RequestId, denial.BlockId);
        }
    }

    private async ValueTask AppendAsync(LayoutRelatedDenial denial)
    {
        var at = time.GetUtcNow();
        // The denied act, typed: reading the target record, by the acting principal, at this instant.
        var request = new AuthorizationWriteContext(new ActorId(denial.PrincipalId), tenant, at)
            .Request(RecordsRead, AuthorizationGate.RecordKindFor(RecordsRead), denial.Target.RecordId);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["requestId"] = denial.RequestId,
            ["principalId"] = denial.PrincipalId,
            ["blockId"] = denial.BlockId,
            ["bindingKind"] = denial.BindingKind,
            ["relationshipKey"] = denial.RelationshipKey,
            ["targetRecordTypeId"] = denial.Target.RecordTypeId,
            ["targetRecordId"] = denial.Target.RecordId,
            ["code"] = denial.Code,
            ["pointer"] = denial.Pointer,
        };
        var payload = await signer.SignAsync(new AuditPayload(body), at, Guid.NewGuid()).ConfigureAwait(false);
        await trail.AppendAsync(new AuditRecord(
            Guid.NewGuid(), tenant, LayoutRelatedDeniedEventType, at, payload, [],
            Actor: request.Principal, Target: request.Target, Act: request.Act)).ConfigureAwait(false);
    }

    /// <summary>The denial a gate-log record stores, or <see langword="null"/> for any other record.</summary>
    public static LayoutRelatedDenial? Denial(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.EventType != LayoutRelatedDeniedEventType) return null;
        var body = record.Payload.Payload.Body;
        // A durable trail rematerializes each value as a JsonElement; its ToString is the stored string.
        string Text(string key) => body.TryGetValue(key, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
        return new LayoutRelatedDenial(Text("requestId"), Text("principalId"), Text("blockId"), Text("bindingKind"),
            Text("relationshipKey"), new LayoutRecordReference(Text("targetRecordTypeId"), Text("targetRecordId")),
            Text("code"), Text("pointer"));
    }
}
