using System;

namespace Harborline.Api.Foundation.Assets.Common;

/// <summary>
/// Stable identifier for a Standing Order. Per ADR 0065 §1.
/// </summary>
/// <remarks>
/// W#48 Phase 1.5 PR 1: moved from <c>Harborline.Api.Foundation.Wayfinder</c>
/// to <c>Harborline.Api.Foundation.Assets.Common</c> to break the
/// <c>ui-core → foundation-wayfinder → kernel-crdt → ui-core</c>
/// cycle. After this move, the <c>Harborline.Api.UICore.Wayfinder.Integrations</c>
/// records can reference <see cref="StandingOrderId"/> via
/// <c>foundation</c> (which <c>ui-core</c> already pulls in) without
/// dragging in <c>foundation-wayfinder</c>.
/// </remarks>
/// <param name="Value">Provider-internal GUID identifier.</param>
public readonly record struct StandingOrderId(Guid Value);

/// <summary>
/// Stable identifier referencing a <c>Harborline.Api.Kernel.Audit.AuditRecord</c>
/// emitted at the time a Standing Order was issued, amended, rescinded,
/// rejected, or conflict-resolved. Audit-record-id round-trips with
/// <c>Harborline.Api.Kernel.Audit.AuditRecord.AuditId</c>. Per ADR 0065 §1.
/// </summary>
/// <remarks>
/// W#48 Phase 1.5 PR 1: relocated alongside <see cref="StandingOrderId"/>.
/// </remarks>
/// <param name="Value">Provider-internal GUID identifier.</param>
public readonly record struct AuditRecordId(Guid Value);
