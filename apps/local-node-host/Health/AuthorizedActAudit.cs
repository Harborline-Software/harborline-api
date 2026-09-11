using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Ticket 331 slice 2 — the sink an ACCEPTED act is recorded through, so the act the caller just performed
/// is addressable: the returned audit id is the id of the entry carrying the very decision that permitted
/// it, and it is what the caller hands to
/// <c>GET /api/local-node/authorization/traces/{auditId}</c> to ask "why was this allowed?".
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE decision, never a second one.</b> Tenant, principal, instant, target and act are copied off
/// <see cref="AuthorizationDecision.Request"/> and from nothing else — the entry cannot name a different
/// act than the one that was decided (the authorized-append guard in <c>AuthorizedAuditRecord</c> rejects
/// the record outright if they disagree), so a trace read of the returned id explains THIS act.
/// </para>
/// <para>
/// <b>FAIL-SAFE-BUT-LOUD</b>, like <see cref="KernelAuditPackInstallAudit"/> and
/// <see cref="AuthorizationRefusalAudit"/>: the mutation is already committed when this runs, so an append
/// fault must not turn the caller's 200 into a 500 — that would make the audit sink a way to fail an act
/// that in fact happened. It is logged as an error and the caller gets <see langword="null"/>, which is the
/// honest answer on the wire: the write stands and carries no addressable id. The fault is NOT raised on
/// <c>/health</c>: health is the node's liveness state, read by the operator CLI on every poll, and one
/// unrecorded append is a gap in one act's disclosure, not a sick node — latching the node unhealthy for it
/// would take the install down for a reporting failure.
/// </para>
/// <para>
/// The payload carries only identifiers the caller already supplied or already holds. A record BODY never
/// reaches it: an audit entry is readable by an auditor who may not read the record.
/// </para>
/// </remarks>
public sealed class AuthorizedActAudit
{
    private readonly IAuthorizedAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly ILogger<AuthorizedActAudit> _logger;

    /// <summary>Constructs the sink over the node's authorized trail and operation signer.</summary>
    public AuthorizedActAudit(
        IAuthorizedAuditTrail trail,
        IOperationSigner signer,
        ILogger<AuthorizedActAudit> logger)
    {
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records <paramref name="decision"/>'s accepted act as <paramref name="eventType"/> and returns the
    /// new entry's audit id, or <see langword="null"/> when the append faulted.
    /// </summary>
    public async ValueTask<Guid?> RecordAsync(
        AuditEventType eventType,
        AuthorizationDecision decision,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(body);
        var request = decision.Request;
        try
        {
            var payload = await _signer.SignAsync(
                new AuditPayload(new Dictionary<string, object?>(body)), request.At, Guid.NewGuid(), ct)
                .ConfigureAwait(false);
            var record = new AuditRecord(
                Guid.NewGuid(),
                request.Tenant,
                eventType,
                request.At,
                payload,
                [],
                Actor: request.Principal,
                Target: request.Target,
                Act: request.Act);
            await _trail.AppendAuthorizedAsync(record, decision, ct).ConfigureAwait(false);
            return record.AuditId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Accepted-act audit append FAILED ({EventType}, tenant {Tenant}, act {Act} on {Kind} {Record}) "
                + "— the act stands but it carries no addressable audit id.",
                eventType.Value, request.Tenant, request.Act.Operation.Value,
                request.Target.RecordKind, request.Target.RecordId);
            return null;
        }
    }
}

/// <summary>Registers <see cref="AuthorizedActAudit"/>.</summary>
public static class AuthorizedActAuditServiceCollectionExtensions
{
    /// <summary>Adds the accepted-act audit sink.</summary>
    public static IServiceCollection AddAuthorizedActAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AuthorizedActAudit>();
        return services;
    }
}
