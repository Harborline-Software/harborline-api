using Harborline.Api.Foundation.SecurityPolicy.Models;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// The greenfield field-class → <see cref="AuditEventClass"/> bridge (ADR 0140 D2 §7).
/// The tenant retention resolver keys off <see cref="AuditEventClass"/>, NOT a field's
/// classification tag; this maps the policy's open-vocab floor-class token to that axis.
/// Kill-trigger: an unmappable floor-class is rejected (no silent default retention window).
/// </summary>
public interface IFieldClassAuditEventClassMap
{
    /// <summary>
    /// Map a floor-class token (e.g. <c>"Identity"</c>, <c>"Financial"</c>) to an
    /// <see cref="AuditEventClass"/>.
    /// </summary>
    /// <exception cref="Enforcement.GovernanceConfigurationException">No mapping exists.</exception>
    AuditEventClass Resolve(string floorClass);
}
