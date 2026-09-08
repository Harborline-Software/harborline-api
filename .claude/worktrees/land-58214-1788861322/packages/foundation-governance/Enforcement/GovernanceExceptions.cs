namespace Harborline.Api.Foundation.Governance.Enforcement;

/// <summary>
/// Thrown when a PEP refuses an operation fail-closed because a data-residency constraint
/// cannot be satisfied — including the eligible-set bridge's fail-closed posture for a
/// classified field that declares no allowed jurisdiction (never the shipped enforcer's
/// fail-open default). ADR 0140 D2 §7 / ADR 0064.
/// </summary>
public sealed class DataResidencyViolationException : Exception
{
    /// <summary>The field whose residency could not be satisfied.</summary>
    public string Field { get; }

    /// <summary>Construct with the offending field + reason.</summary>
    public DataResidencyViolationException(string field, string reason)
        : base($"Data-residency violation on field '{field}': {reason}")
        => Field = field;
}

/// <summary>
/// Thrown when a Read / Export PEP refuses fail-closed because a required consent record is
/// absent (ADR 0140 D2 §7; the <c>Consent</c> effect).
/// </summary>
public sealed class ConsentRequiredException : Exception
{
    /// <summary>The field whose read/export was blocked.</summary>
    public string Field { get; }

    /// <summary>Why the consent gate refused — the decision's own reason, not a re-derived one.</summary>
    public Consent.ConsentRefusal Refusal { get; }

    /// <summary>Construct with the field, the purpose, and the refusal the consent gate decided.</summary>
    public ConsentRequiredException(string field, string purpose, Consent.ConsentRefusal refusal)
        : base($"Consent required for field '{field}' (purpose '{purpose}'): {refusal}.")
    {
        Field = field;
        Refusal = refusal;
    }
}

/// <summary>
/// Thrown when an EraseSubject PEP is blocked because a legal hold or an unexpired
/// retention floor is in effect — the §3.3 lattice (legal-hold &gt; retention-floor &gt; erase).
/// </summary>
public sealed class RetentionHoldException : Exception
{
    /// <summary>Construct with the reason the erasure was held.</summary>
    public RetentionHoldException(string reason)
        : base($"Subject erasure blocked by an active hold: {reason}") { }
}

/// <summary>
/// Thrown when a policy references configuration that does not exist — e.g. a
/// <c>Retain</c> effect whose floor-class string maps to no <c>AuditEventClass</c>
/// (the class→AuditEventClass kill-trigger: no silent default retention window).
/// </summary>
public sealed class GovernanceConfigurationException : Exception
{
    /// <summary>Construct with the configuration error.</summary>
    public GovernanceConfigurationException(string message) : base(message) { }
}
