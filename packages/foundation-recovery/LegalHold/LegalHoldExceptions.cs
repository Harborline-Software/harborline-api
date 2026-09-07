using System;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// Thrown by <see cref="ILegalHoldService.ReleaseAsync"/> when a release request
/// fails the multi-actor approval floor (ADR 0068 §3.1 / ADR 0142 §D2) or names a
/// hold that does not exist or is already released. Fail-closed: a rejected release
/// leaves the hold ACTIVE — the subject stays protected from shred.
/// </summary>
public sealed class LegalHoldReleaseRejectedException : Exception
{
    /// <summary>Construct with the rejection reason.</summary>
    public LegalHoldReleaseRejectedException(string reason)
        : base($"Legal-hold release rejected: {reason}")
    {
        Reason = reason;
    }

    /// <summary>Short machine-stable rejection reason (e.g. <c>"approval floor not met"</c>, <c>"hold not found"</c>, <c>"already released"</c>).</summary>
    public string Reason { get; }
}
