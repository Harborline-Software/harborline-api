using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Audit;

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
