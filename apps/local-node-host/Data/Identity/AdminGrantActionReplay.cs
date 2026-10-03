using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

public sealed class GrantActionReplayConflictException()
    : InvalidOperationException("The correlation belongs to a different grant action.");

internal sealed partial class AdminTeamAccessAuthority
{
    private readonly SemaphoreSlim _grantActionGate = new(1, 1);

    private async Task<T> SerializedGrantActionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _grantActionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _grantActionGate.Release(); }
    }

    private async ValueTask<AuditRecord?> CorrelatedGrantReplayAsync(AuthorizationDecision decision,
        string reason, AuditEventType eventType, CancellationToken ct)
    {
        if (decision.Request.CorrelationId is not { } correlation) return null;
        AuditRecord? replay = null;
        await foreach (var row in _audit.QueryAsync(new AuditQuery(decision.Request.Tenant), ct).ConfigureAwait(false))
        {
            if (AuditText(row, "correlation_id") != correlation.ToString("D")) continue;
            if (row.Actor != decision.Request.Principal || row.Target != decision.Request.Target ||
                row.Act != decision.Request.Act || AuditText(row, "reason") != reason)
                throw new GrantActionReplayConflictException();
            if (row.EventType == eventType) replay = row;
        }
        // The mutation may have committed while its signed audit is still owed. That committed record is
        // the same replay receipt as its delivered form; do not mutate the grant or mint another audit id.
        await using var db = await _grantFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Published receipts are retained too: a concurrent drain may publish between the two reads above/below.
        var receipts = await db.AuditOutbox.AsNoTracking()
            .Where(row => row.TenantId == decision.Request.Tenant.Value)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in receipts)
        {
            using var body = JsonDocument.Parse(row.BodyJson);
            if (!body.RootElement.TryGetProperty("correlation_id", out var rowCorrelation)
                || rowCorrelation.ToString() != correlation.ToString("D")) continue;
            if (row.Actor != decision.Request.Principal.Value || row.TargetKind != decision.Request.Target.RecordKind
                || row.TargetId != decision.Request.Target.RecordId || row.TargetScope != decision.Request.Target.Scope.Value
                || row.Act != decision.Request.Act.ToString()
                || !body.RootElement.TryGetProperty("reason", out var rowReason) || rowReason.ToString() != reason)
                throw new GrantActionReplayConflictException();
            if (row.EventType != eventType.Value) continue;
            var payload = NodeAuditRecordJson.ReadPayload(row.SignedPayloadJson
                ?? throw new InvalidOperationException("A committed grant replay receipt must carry its signed payload."));
            replay = new AuditRecord(Guid.Parse(row.AuditId), decision.Request.Tenant, eventType, row.OccurredAt,
                payload, [], Actor: decision.Request.Principal, Target: decision.Request.Target, Act: decision.Request.Act);
        }
        return replay;
    }

    private async ValueTask<AuditRecord?> OriginalGrantAuditAsync(AuthorizationDecision decision,
        AuditEventType eventType, string reason, CancellationToken ct)
    {
        await foreach (var row in _audit.QueryAsync(new AuditQuery(decision.Request.Tenant, eventType), ct).ConfigureAwait(false))
        {
            if (row.Target != decision.Request.Target || AuditText(row, "reason") != reason) continue;
            if (decision.Request.CorrelationId is { } correlation &&
                (AuditText(row, "correlation_id") != correlation.ToString("D") || row.Actor != decision.Request.Principal))
                throw new GrantActionReplayConflictException();
            return row;
        }
        return null;
    }

    private static string? AuditText(AuditRecord row, string key) =>
        row.Payload.Payload.Body.TryGetValue(key, out var value) ? value?.ToString() : null;
    private static Guid? AuditCorrelation(AuditRecord row) =>
        Guid.TryParse(AuditText(row, "correlation_id"), out var value) ? value : null;
}
