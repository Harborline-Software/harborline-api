using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Kernel.Audit.Payloads;

/// <summary>
/// Typed payload factories for the enrollment compensating-control <see cref="AuditEventType"/> cohort (enrollment
/// Phase C; <c>project_sod_compensating_controls</c>). These describe the duty-significant ENROLLMENT
/// operations — membership admit/revoke, permission grant, ownership transfer — and the audit-export act
/// itself. They land in the SAME unified <see cref="IAuditTrail"/> the financial posts already use, so a
/// reviewer sees membership + permission changes alongside financial activity (the "second set of eyes").
/// </summary>
/// <remarks>
/// <para>
/// <b>The separation-of-duty goal is universal even solo</b> (CIC 2026-06-20): the platform PROVIDES the compensating
/// controls so a lone operator gets disaster-recovery, fraud-protection-via-tracking, and
/// delegation-readiness without dividing the work. As people are added, the SAME audit trail + PBAC model
/// scale to divide-the-work separation of duty (composition constraints).
/// </para>
/// <para>
/// <b>No secrets in payloads.</b> Mirroring <see cref="SecurityPolicyAuditPayloads"/>: the audit trail is the
/// trans-tenant, long-retention, reviewer-facing compliance surface. Raw private keys, raw signature bytes,
/// and any other sensitive material MUST NEVER appear here. Public keys (base64url) and permission STRINGS
/// are non-sensitive identifiers and are fine to carry — they ARE the duty-relevant facts a reviewer needs.
/// </para>
/// <para>
/// <b>Carry into <see cref="AuditPayload.Body"/>.</b> Each record exposes a <c>ToBody()</c> method producing
/// the opaque dictionary the audit substrate stores; the typed record is the producer-side ergonomic shape.
/// </para>
/// </remarks>
public static class EnrollmentCompensatingControlPayloads
{
    /// <summary>
    /// Payload for <see cref="AuditEventType.MemberAdmitted"/>. Records WHO admitted WHOM, with which
    /// granted-permission strings, in which team.
    /// </summary>
    /// <param name="TenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="TeamId">The team (roster) the member joined.</param>
    /// <param name="AdmitterPartyId">The in-roster admin who signed the admission (held <c>members:admit</c>).</param>
    /// <param name="AdmittedPartyId">The party that was admitted.</param>
    /// <param name="AdmittedPublicKeyBase64Url">The admitted member's bound public key (base64url; non-secret).</param>
    /// <param name="GrantedPermissions">The permission strings the admitted member received (subset-bounded by no-escalation).</param>
    /// <param name="AdmissionMode">The admission channel — e.g. <c>proximity</c> or <c>invite</c>.</param>
    /// <param name="CorrelationId">Optional correlation-id linking this to the originating request.</param>
    public sealed record MemberAdmittedPayload(
        TenantId TenantId,
        string TeamId,
        string AdmitterPartyId,
        string AdmittedPartyId,
        string AdmittedPublicKeyBase64Url,
        IReadOnlyList<string> GrantedPermissions,
        string AdmissionMode,
        string? CorrelationId = null)
    {
        /// <summary>Project to the opaque audit-body dictionary the substrate stores.</summary>
        public IReadOnlyDictionary<string, object?> ToBody() => new Dictionary<string, object?>
        {
            ["tenant_id"] = TenantId.Value,
            ["team_id"] = TeamId,
            ["admitter_party_id"] = AdmitterPartyId,
            ["admitted_party_id"] = AdmittedPartyId,
            ["admitted_public_key"] = AdmittedPublicKeyBase64Url,
            ["granted_permissions"] = GrantedPermissions,
            ["admission_mode"] = AdmissionMode,
            ["correlation_id"] = CorrelationId,
        };
    }

    /// <summary>
    /// Payload for <see cref="AuditEventType.MemberRevoked"/>. Records WHO revoked WHOM. The admission stays
    /// in the immutable log (genesis-vs-live) — this records the LIVE-state removal.
    /// </summary>
    /// <param name="TenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="TeamId">The team (roster) the member was revoked from.</param>
    /// <param name="RevokerPartyId">The in-roster admin who revoked (held <c>members:revoke</c>).</param>
    /// <param name="RevokedPartyId">The party removed from live state.</param>
    /// <param name="CorrelationId">Optional correlation-id linking this to the originating request.</param>
    public sealed record MemberRevokedPayload(
        TenantId TenantId,
        string TeamId,
        string RevokerPartyId,
        string RevokedPartyId,
        string? CorrelationId = null)
    {
        /// <summary>Project to the opaque audit-body dictionary the substrate stores.</summary>
        public IReadOnlyDictionary<string, object?> ToBody() => new Dictionary<string, object?>
        {
            ["tenant_id"] = TenantId.Value,
            ["team_id"] = TeamId,
            ["revoker_party_id"] = RevokerPartyId,
            ["revoked_party_id"] = RevokedPartyId,
            ["correlation_id"] = CorrelationId,
        };
    }

    /// <summary>
    /// Payload for <see cref="AuditEventType.PermissionsGranted"/>. Records the WHO/WHOM/WHAT of a
    /// fine-grained permission change on a member's roster edge.
    /// </summary>
    /// <param name="TenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="TeamId">The team (roster) the grant occurred in.</param>
    /// <param name="GranterPartyId">The member who granted (held <c>grant:permissions</c>).</param>
    /// <param name="TargetPartyId">The member whose permission set changed.</param>
    /// <param name="ResultingPermissions">The target's permission strings AFTER the grant (subset-bounded by no-escalation).</param>
    /// <param name="CorrelationId">Optional correlation-id linking this to the originating request.</param>
    public sealed record PermissionsGrantedPayload(
        TenantId TenantId,
        string TeamId,
        string GranterPartyId,
        string TargetPartyId,
        IReadOnlyList<string> ResultingPermissions,
        string? CorrelationId = null)
    {
        /// <summary>Project to the opaque audit-body dictionary the substrate stores.</summary>
        public IReadOnlyDictionary<string, object?> ToBody() => new Dictionary<string, object?>
        {
            ["tenant_id"] = TenantId.Value,
            ["team_id"] = TeamId,
            ["granter_party_id"] = GranterPartyId,
            ["target_party_id"] = TargetPartyId,
            ["resulting_permissions"] = ResultingPermissions,
            ["correlation_id"] = CorrelationId,
        };
    }

    /// <summary>
    /// Payload for <see cref="AuditEventType.OwnershipTransferred"/>. The root-grant holdership changed hands
    /// (the no-bricking-floor satisfier). The most-privileged authority moving is the single most
    /// duty-significant enrollment event.
    /// </summary>
    /// <param name="TenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="TeamId">The team (roster) the transfer occurred in.</param>
    /// <param name="FromPartyId">The party that held the root grant before.</param>
    /// <param name="ToPartyId">The party that holds the root grant after.</param>
    /// <param name="CorrelationId">Optional correlation-id linking this to the originating request.</param>
    public sealed record OwnershipTransferredPayload(
        TenantId TenantId,
        string TeamId,
        string FromPartyId,
        string ToPartyId,
        string? CorrelationId = null)
    {
        /// <summary>Project to the opaque audit-body dictionary the substrate stores.</summary>
        public IReadOnlyDictionary<string, object?> ToBody() => new Dictionary<string, object?>
        {
            ["tenant_id"] = TenantId.Value,
            ["team_id"] = TeamId,
            ["from_party_id"] = FromPartyId,
            ["to_party_id"] = ToPartyId,
            ["correlation_id"] = CorrelationId,
        };
    }

    /// <summary>
    /// Payload for <see cref="AuditEventType.AuditExported"/>. Records that the audit trail was delivered to
    /// an INDEPENDENT reviewer (the "Direct Bank Delivery" compensating control). Carries the destination
    /// label + the bounds of what was exported, NOT the exported record bodies themselves.
    /// </summary>
    /// <param name="TenantId">The tenant whose audit-stream was exported.</param>
    /// <param name="ExporterPartyId">The party that triggered the export (held <c>telemetry:export</c>).</param>
    /// <param name="DestinationLabel">A non-secret label for the reviewer/SIEM destination (e.g. the configured endpoint name — never credentials).</param>
    /// <param name="RecordCount">The number of audit records the destination ACCEPTED (0 when the sink faulted before/after accepting nothing verifiable).</param>
    /// <param name="From">Optional inclusive lower bound on the exported window.</param>
    /// <param name="To">Optional inclusive upper bound on the exported window.</param>
    /// <param name="CorrelationId">Optional correlation-id linking this to the originating request.</param>
    /// <param name="Failed">True when the export sink FAULTED (a non-conformant sink that threw instead of honoring its never-throw contract). The export ACT is still recorded so the attempt is auditable (#1295 F3); the records may have been partially or not delivered.</param>
    /// <param name="FaultMessage">When <paramref name="Failed"/>, a non-secret summary of the sink fault (the exception message — never credentials/keys). Null on the success path.</param>
    public sealed record AuditExportedPayload(
        TenantId TenantId,
        string ExporterPartyId,
        string DestinationLabel,
        int RecordCount,
        DateTimeOffset? From = null,
        DateTimeOffset? To = null,
        string? CorrelationId = null,
        bool Failed = false,
        string? FaultMessage = null)
    {
        /// <summary>Project to the opaque audit-body dictionary the substrate stores.</summary>
        public IReadOnlyDictionary<string, object?> ToBody() => new Dictionary<string, object?>
        {
            ["tenant_id"] = TenantId.Value,
            ["exporter_party_id"] = ExporterPartyId,
            ["destination_label"] = DestinationLabel,
            ["record_count"] = RecordCount,
            ["from"] = From?.ToString("O"),
            ["to"] = To?.ToString("O"),
            ["correlation_id"] = CorrelationId,
            ["failed"] = Failed,
            ["fault_message"] = FaultMessage,
        };
    }
}
