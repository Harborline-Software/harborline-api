using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// The greenfield legal-hold registry (ADR 0140 D2 §7 / ADR 0137 §D8). A subject under an
/// active legal hold cannot be crypto-shredded — the EraseSubject PEP consults this gate
/// FIRST. Kill-trigger: no EraseSubject PEP ships until this hold-gate exists.
/// </summary>
public interface ILegalHoldRegistry
{
    /// <summary>True when <paramref name="subject"/> in <paramref name="tenant"/> is under an active legal hold.</summary>
    bool IsHeld(TenantId tenant, SubjectId subject);
}
