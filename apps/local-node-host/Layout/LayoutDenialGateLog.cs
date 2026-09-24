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

    private readonly List<Task> _appends = [];

    /// <inheritdoc />
    public void RecordDenial(LayoutRelatedDenial denial)
    {
        ArgumentNullException.ThrowIfNull(denial);
        // The append starts here, at the act, and runs synchronously up to its first incomplete await;
        // the platform trace is synchronous, so the host awaits the rest through WrittenAsync.
        _appends.Add(AppendAsync(denial));
    }

    /// <summary>Completes every append this request started. The host awaits it before it answers.</summary>
    public Task WrittenAsync() => Task.WhenAll(_appends);

    private async Task AppendAsync(LayoutRelatedDenial denial)
    {
        try
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Layout related-binding denial append FAILED (tenant {Tenant}, request {RequestId}, block {BlockId}); "
                + "the viewer still sees absence but the gate log has no record.",
                tenant, denial.RequestId, denial.BlockId);
        }
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

/// <summary>
/// DES-0052 layout-run-5, host half (T-731): the only reader of Layout's related-binding denials.
/// It finds them by authored binding (block and relationship) plus request, and returns one only to a
/// reader the gate allows both <c>audit:read</c> on that gate-log entry and <c>records:read</c> on
/// the denied record. It never re-evaluates the denial; it returns what was stored at the act.
/// </summary>
/// <remarks>A reader lacking either authorization gets an empty list, the same answer as when no
/// denial exists, so the read is not an oracle for the record either.</remarks>
public sealed class LayoutDenialReader(IAuditTrail trail, AuthorizationGate gate)
{
    private static readonly AuthorizationOperation AuditRead = AuthorizationOperation.Parse(Permission.AuditRead);
    private static readonly AuthorizationOperation RecordsRead = AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead);

    /// <summary>The denials recorded for <paramref name="blockId"/>'s <paramref name="relationshipKey"/>
    /// in <paramref name="requestId"/> that <paramref name="reader"/> may read at <paramref name="at"/>.</summary>
    public async ValueTask<IReadOnlyList<LayoutRelatedDenial>> ReadAsync(
        TenantId tenant, ActorId reader, string blockId, string relationshipKey, string requestId,
        DateTimeOffset at, CancellationToken ct = default)
    {
        var context = new AuthorizationWriteContext(reader, tenant, at);
        var found = new List<LayoutRelatedDenial>();
        await foreach (var record in trail
            .QueryAsync(new AuditQuery(tenant, LayoutDenialGateLog.LayoutRelatedDeniedEventType), ct).ConfigureAwait(false))
        {
            if (LayoutDenialGateLog.Denial(record) is not { } denial
                || denial.BlockId != blockId || denial.RelationshipKey != relationshipKey || denial.RequestId != requestId)
                continue;
            if (await AllowedAsync(context, AuditRead, record.AuditId.ToString(), ct).ConfigureAwait(false)
                && await AllowedAsync(context, RecordsRead, denial.Target.RecordId, ct).ConfigureAwait(false))
                found.Add(denial);
        }
        return found;
    }

    private async ValueTask<bool> AllowedAsync(
        AuthorizationWriteContext context, AuthorizationOperation operation, string recordId, CancellationToken ct)
        => (await gate.DecideAsync(context.Request(operation, AuthorizationGate.RecordKindFor(operation), recordId), ct)
            .ConfigureAwait(false)).Verdict == AuthorizationVerdict.Allowed;
}
