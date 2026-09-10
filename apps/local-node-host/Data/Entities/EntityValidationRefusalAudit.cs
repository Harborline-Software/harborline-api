using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// Ticket 151 slice 2 row 4d — a record write refused by the validator leaves a machine-readable
/// entry where the decision trace reads it.
/// </summary>
/// <remarks>
/// <para>
/// The entry is <see cref="AuthorizationPreDecisionRefusal"/>-shaped on purpose: the act HAD an
/// allowed gate decision, so it is not an authorization refusal, but the trace reader already
/// projects that shape and a reviewer should see a refused write in the same column as every other
/// refusal. It carries the reason code, the failing RFC 6901 pointers and the remediation —
/// <b>never</b> the body, which is exactly what <see cref="EntityValidationException"/> was given a
/// code and pointers for.
/// </para>
/// <para>
/// Fail-safe but loud, like <see cref="Health.AuthorizationRefusalAudit"/>: the write is refused
/// either way, so an append fault is logged and swallowed rather than turned into a 500 (which would
/// make the audit sink a way to change the refusal).
/// </para>
/// </remarks>
public sealed class EntityValidationRefusalAudit(
    IAuditTrail trail,
    IOperationSigner signer,
    ILogger<EntityValidationRefusalAudit> logger)
{
    /// <summary>The event type a refused record write is recorded under.</summary>
    public static readonly AuditEventType RecordWriteRefusedEventType = new("RecordWriteValidationRefused");

    private const string RemediationText =
        "Correct the body at the pointers this entry names and write again.";

    /// <summary>Records <paramref name="refusal"/> against the act that carried it.</summary>
    public async ValueTask RecordAsync(
        EntityValidationException refusal,
        AuthorizationWriteContext authority,
        string recordId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        try
        {
            var detail = refusal.Pointers.Count == 0
                ? refusal.ReasonCode
                : $"{refusal.ReasonCode} at {string.Join(", ", refusal.Pointers)}";
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = refusal.ReasonCode,
                ["permission"] = TeamRolePermissions.RecordsWrite,
                ["pointers"] = refusal.Pointers,
                ["preDecision"] = false,
                ["preDecisionRefusal"] = new AuthorizationPreDecisionRefusal(
                    refusal.ReasonCode, detail, RemediationText),
            };
            var payload = await signer.SignAsync(new AuditPayload(body), authority.At, Guid.NewGuid(), ct)
                .ConfigureAwait(false);
            await trail.AppendAsync(
                new AuditRecord(
                    AuditId: Guid.NewGuid(),
                    TenantId: authority.Tenant,
                    EventType: RecordWriteRefusedEventType,
                    OccurredAt: authority.At,
                    Payload: payload,
                    AttestingSignatures: [],
                    Actor: authority.Principal,
                    Target: new AuthorizationTarget("record", recordId, ScopeExpression.Parse($"/records/{recordId}")),
                    Act: null),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Record-write validation refusal audit append FAILED (tenant {Tenant}, record {Record}) — "
                + "the write was still refused but its audit row was not written.",
                authority.Tenant, recordId);
        }
    }
}

/// <summary>
/// The one wrapper every host record write admits through, so the row 4d entry cannot be forgotten at
/// a call site: <see cref="EntityBodyAdmission.AdmitAsync"/> plus the refusal's audit entry.
/// </summary>
internal static class AuditedEntityBodyAdmission
{
    internal static async ValueTask<ValidatedBody> AdmitAuditedAsync(
        this EntityBodyAdmission admission,
        EntityValidationRefusalAudit? audit,
        IWriteAdmission allowed,
        SchemaId schema,
        System.Text.Json.JsonDocument body,
        AuthorizationWriteContext authority,
        string recordId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admission);
        try
        {
            return await admission.AdmitAsync(allowed, schema, body, ct).ConfigureAwait(false);
        }
        catch (EntityValidationException refusal)
        {
            if (audit is not null)
                await audit.RecordAsync(refusal, authority, recordId, ct).ConfigureAwait(false);
            throw;
        }
    }
}
