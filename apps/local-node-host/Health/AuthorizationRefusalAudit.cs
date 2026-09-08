using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The audit sink for an authorization refusal (ticket 214 slice 2, ledger L656). Slice 1 made the
/// renderer keep the CLASSIFIED reading — the full reason and every field the response redacted — on
/// <see cref="AuthorizationRefusal.Diagnostic"/>; this is what that reading is kept FOR. The refusal's
/// audit row carries the diagnostic verbatim, so the reviewer sees what the caller was not allowed to.
/// </summary>
/// <remarks>
/// <para>
/// A decided refusal uses <c>IRefusedAuditTrail</c>, which validates and copies its denied decision.
/// The allowed-append guard remains unchanged. Pre-decision refusals use the ordinary append. The decision's own facts — principal, tenant, instant, target, act — are copied
/// from the ONE decision the guard already made and never re-derived; a refusal that never reached a
/// decision is recorded with <c>preDecision = true</c> instead of an invented one.
/// </para>
/// <para>
/// FAIL-SAFE-BUT-LOUD, like <see cref="KernelAuditPackInstallAudit"/>: an append fault must not turn a 403
/// into a 500 (that would make the audit sink a way to change the refusal), but it is logged as an error.
/// </para>
/// </remarks>
public sealed class AuthorizationRefusalAudit
{
    /// <summary>The refusal event type on the unified trail.</summary>
    public static readonly AuditEventType AuthorizationRefusedEventType = new("AuthorizationRefused");

    /// <summary>A previously reported refusal no longer applies.</summary>
    public static readonly AuditEventType AuthorizationRefusalClearedEventType = new("AuthorizationRefusalCleared");

    private readonly IAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly ILogger<AuthorizationRefusalAudit> _logger;

    /// <summary>Constructs the sink over the unified audit trail and the node's operation signer.</summary>
    public AuthorizationRefusalAudit(
        IAuditTrail trail,
        IOperationSigner signer,
        ILogger<AuthorizationRefusalAudit> logger)
    {
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The payload key the classified diagnostic is recorded under.</summary>
    public const string DiagnosticKey = "diagnostic";

    /// <summary>
    /// Records <paramref name="refusal"/>. <paramref name="decision"/> is the very decision the guard
    /// made, or <see langword="null"/> when the act never reached one.
    /// </summary>
    public ValueTask<Guid?> RecordAsync(
        AuthorizationRefusal refusal,
        string permission,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationDecision? decision,
        CancellationToken ct = default) =>
        RecordCoreAsync(refusal, permission, principal, tenant, at, decision, AuthorizationRefusedEventType, ct);

    internal async ValueTask RecordAsync(AuthorizationDecision decision, CancellationToken ct)
    {
        if (decision.Verdict != AuthorizationVerdict.Denied) return;
        var refusal = await AuthorizationRefusalRenderer.RenderAsync(decision, [], null, ct).ConfigureAwait(false);
        var request = decision.Request;
        await RecordAsync(refusal, request.Act.Operation.Value, request.Principal, request.Tenant,
            request.At, decision, ct).ConfigureAwait(false);
    }

    /// <summary>Records the clearing of a previously reported refusal, retaining its original diagnostic.</summary>
    public ValueTask<Guid?> RecordClearedAsync(
        AuthorizationRefusal refusal,
        string permission,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct = default) =>
        RecordCoreAsync(refusal, permission, principal, tenant, at, null, AuthorizationRefusalClearedEventType, ct);

    private async ValueTask<Guid?> RecordCoreAsync(
        AuthorizationRefusal refusal,
        string permission,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationDecision? decision,
        AuditEventType eventType,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        try
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = refusal.Code,
                ["permission"] = permission,
                ["remedy"] = refusal.Remediation,
                ["preDecision"] = decision is null,
                ["preDecisionRefusal"] = decision is null && refusal.Code == MemberRoster.NoBrickingFloorCode
                    ? new AuthorizationPreDecisionRefusal(refusal.Code, refusal.Detail, refusal.Remediation) : null,
                [DiagnosticKey] = refusal.Diagnostic,
                ["decisionEvidence"] = decision?.Evidence.Project(),
            };
            var payload = await _signer.SignAsync(new AuditPayload(body), at, Guid.NewGuid())
                .ConfigureAwait(false);
            var record = new AuditRecord(
                AuditId: Guid.NewGuid(),
                TenantId: tenant,
                EventType: eventType,
                OccurredAt: at,
                Payload: payload,
                AttestingSignatures: [],
                Actor: principal,
                Target: decision?.Request.Target,
                Act: decision?.Request.Act);
            if (decision is not null)
                await ((IRefusedAuditTrail)_trail).AppendRefusedAsync(record, decision, ct).ConfigureAwait(false);
            else
                await _trail.AppendAsync(record, ct).ConfigureAwait(false);
            return record.AuditId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-safe-but-LOUD: the refusal stands either way; a refusal that could not be recorded is
            // a security-relevant gap, not a reason to answer the caller differently.
            _logger.LogError(ex,
                "Authorization refusal audit append FAILED (tenant {Tenant}, permission {Permission}) — the "
                + "act was still refused but its audit row was not written.",
                tenant, permission);
            return null;
        }
    }
}

/// <summary>Composition for <see cref="AuthorizationRefusalAudit"/> — one registration, shared by the
/// shipping host and by the route tests, so there is no test/production wiring drift.</summary>
public static class AuthorizationRefusalAuditComposition
{
    /// <summary>Registers the refusal audit sink. Requires <see cref="IAuditTrail"/> and
    /// <see cref="IOperationSigner"/> (the enrollment compensating-control audit composition and Program.cs
    /// register both).</summary>
    public static IServiceCollection AddAuthorizationRefusalAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AuthorizationRefusalAudit>();
        services.TryAddScoped<AuthorizationTraceReader>();
        return services;
    }
}
