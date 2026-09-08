using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Engine.Exceptions;

/// <summary>
/// Raised by <see cref="IFormEngine.RenderAsync"/> when the requested instance
/// cannot be returned to the active capability — because it does not exist, has
/// been soft-deleted, or belongs to a different tenant than the token
/// (INV-S1 fail-closed).
/// </summary>
/// <remarks>
/// <para>
/// <b>The three causes are deliberately indistinguishable.</b> A cross-tenant
/// instance and a non-existent instance both surface as this same exception with
/// the same message, so a caller cannot probe one tenant's id space from another
/// tenant's capability. The message carries only the opaque
/// <see cref="InstanceId"/> the caller already supplied — never any portion of
/// the instance body, schema, or owning tenant.
/// </para>
/// </remarks>
public sealed class FormInstanceNotFoundException : Exception
{
    /// <summary>Creates the exception for the given instance id.</summary>
    public FormInstanceNotFoundException(EntityId instanceId)
        : base($"Form instance '{instanceId}' was not found for the active capability.")
    {
        InstanceId = instanceId;
    }

    /// <summary>The instance id the caller requested (the only datum echoed).</summary>
    public EntityId InstanceId { get; }
}

/// <summary>
/// Raised by <see cref="IFormEngine.SaveAsync"/> when the candidate document
/// fails schema or resource-bound validation. The structured
/// <see cref="Result"/> carries the per-error detail;
/// <see cref="IFormEngine.ValidateAsync"/> returns the same
/// <see cref="ValidationResult"/> without throwing, so callers that want to
/// inspect failures before committing can validate first.
/// </summary>
public sealed class FormValidationException : Exception
{
    /// <summary>Creates the exception from a failed validation result.</summary>
    public FormValidationException(ValidationResult result)
        : base($"Form candidate failed validation with {result.Errors.Count} error(s).")
    {
        Result = result;
    }

    /// <summary>The structured validation result (always <c>IsValid == false</c>).</summary>
    public ValidationResult Result { get; }
}

/// <summary>
/// Raised by <see cref="ProjectingFormEngine.SaveWithReceiptAsync"/> when a post-submit projection
/// fails AFTER the submission has already committed (ADR 0101 Rev 3.1 Wave 2b / F-ROUTE). The
/// submission itself is durable — persisted, audited, and recorded in the at-least-once projection
/// outbox (a <c>Failed</c> or <c>Pending</c> row the reconcile sweep will heal). This exception carries
/// the COMMITTED <see cref="Receipt"/> so a route can return a success-with-pending receipt to the
/// client instead of a retry-inviting HTTP 500 for a submission that actually succeeded; the underlying
/// projection fault is the <see cref="Exception.InnerException"/> for diagnosis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Boundary (do not blur).</b> This wraps ONLY a failure of the projection step, which runs strictly
/// AFTER the inner engine has committed the instance. A PRE-commit failure — validation, capability,
/// definition-not-found, an idempotency conflict — is NOT wrapped: it propagates unchanged so the caller
/// keeps its existing error semantics (there is no committed submission to defer). A retry of a
/// committed-but-pending submission with the same idempotency key resolves to the SAME instance
/// (deterministic id), so surfacing pending-success can never double-submit.
/// </para>
/// </remarks>
public sealed class FormSubmitProjectionPendingException : Exception
{
    /// <summary>Creates the exception carrying the committed receipt and the underlying projection fault.</summary>
    public FormSubmitProjectionPendingException(FormSubmitReceipt receipt, Exception innerException)
        : base(
            $"Form submission '{receipt.InstanceId}' committed, but its post-submit projection did not " +
            "complete; the durable outbox row will be reconciled.",
            innerException)
    {
        Receipt = receipt;
    }

    /// <summary>The committed submission's receipt — the instance exists and is durable.</summary>
    public FormSubmitReceipt Receipt { get; }
}

/// <summary>
/// Raised by <see cref="IFormEngine.SaveAsync"/> when the SPINE-2 governance layer
/// (ADR 0140 D2) refuses to persist a classified field fail-closed — because the
/// field carries a classification tag that resolves to no enforceable policy at all
/// (F-11: a class-required field with no resolvable policy is refused, never stored
/// default-allowed). Distinct from a residency / consent / configuration refusal
/// (those surface as the governance layer's own
/// <c>DataResidencyViolationException</c> / <c>GovernanceConfigurationException</c>,
/// which the engine lets propagate unchanged).
/// </summary>
public sealed class FormGovernanceEnforcementException : Exception
{
    /// <summary>Creates the exception for the offending field + the fail-closed reason.</summary>
    public FormGovernanceEnforcementException(string field, string reason)
        : base($"Governance enforcement refused field '{field}': {reason}")
    {
        Field = field;
    }

    /// <summary>The field whose classification could not be enforced.</summary>
    public string Field { get; }
}
