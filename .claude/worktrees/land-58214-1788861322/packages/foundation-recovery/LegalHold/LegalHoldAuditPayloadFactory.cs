using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// Builds the <see cref="AuditPayload"/> bodies for the legal-hold lifecycle events
/// (ADR 0142 §D6): <see cref="AuditEventType.LegalHoldPlaced"/>,
/// <see cref="AuditEventType.LegalHoldReleased"/>, and
/// <see cref="AuditEventType.SubjectShredBlockedByLegalHold"/>. Payloads carry the
/// tenant, the held reference, the matter, and the acting actor(s) — never any
/// erased or held plaintext.
/// </summary>
internal static class LegalHoldAuditPayloadFactory
{
    /// <summary>Body for <see cref="AuditEventType.LegalHoldPlaced"/>.</summary>
    public static AuditPayload Placed(LegalHoldEntry entry) =>
        new(new Dictionary<string, object?>
        {
            ["tenant"] = entry.TenantId.Value,
            ["hold_id"] = entry.HoldId.Value,
            ["held_ref"] = entry.HeldRef.Canonical,
            ["matter"] = entry.Matter,
            ["placed_by"] = entry.PlacedBy.Value,
            ["placed_at"] = entry.PlacedAtUtc,
        });

    /// <summary>Body for <see cref="AuditEventType.LegalHoldReleased"/>.</summary>
    public static AuditPayload Released(LegalHoldRelease release) =>
        new(new Dictionary<string, object?>
        {
            ["tenant"] = release.TenantId.Value,
            ["hold_id"] = release.HoldId.Value,
            ["approving_actors"] = release.Approvers.Select(a => a.Value).ToArray(),
            ["reason"] = release.Reason,
            ["released_at"] = release.ReleasedAtUtc,
        });

    /// <summary>Body for <see cref="AuditEventType.SubjectShredBlockedByLegalHold"/> (the attempt + block).</summary>
    public static AuditPayload ShredBlocked(TenantId tenant, SubjectId subject) =>
        new(new Dictionary<string, object?>
        {
            ["tenant"] = tenant.Value,
            ["subject_pseudonym"] = SubjectPseudonym.Derive(tenant, subject),
        });
}
