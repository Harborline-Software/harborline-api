using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// Authorized audit append surface. The supplied decision is the exact immutable value that admitted the
/// mutation; implementations copy its authority facts and never reconstruct authority from ambient state.
/// </summary>
public interface IAuthorizedAuditTrail : IAuditTrail
{
    /// <summary>
    /// Appends an authorized act using the already-allowed carried decision. <paramref name="approval"/> is
    /// the ADR 0067 clause 2 separation-of-duty decision when the act was an approval — the five approval
    /// facts are copied from it and from nothing else, and a REFUSED approval is recorded just as faithfully
    /// as an approved one. A writer with no approval decision passes none; it never invents one.
    /// </summary>
    ValueTask AppendAuthorizedAsync(
        AuditRecord record,
        AuthorizationDecision decision,
        CancellationToken ct = default,
        SeparationOfDutyDecision? approval = null);
}

/// <summary>Stable refusal codes for malformed carried authorization evidence.</summary>
public static class AuthorizedAuditRefusalCodes
{
    public const string DecisionDenied = "audit.authority.decision_denied";
    public const string TenantMismatch = "audit.authority.tenant_mismatch";
    public const string PrincipalMismatch = "audit.authority.principal_mismatch";
    public const string InstantMismatch = "audit.authority.instant_mismatch";
    public const string TargetMismatch = "audit.authority.target_mismatch";
    public const string ActMismatch = "audit.authority.act_mismatch";
}

/// <summary>Raised before persistence when carried authorization evidence cannot describe the record.</summary>
public sealed class AuthorizedAuditRefusedException(string code)
    : InvalidOperationException($"Authorized audit append refused: {code}.")
{
    public string Code { get; } = code;
}
