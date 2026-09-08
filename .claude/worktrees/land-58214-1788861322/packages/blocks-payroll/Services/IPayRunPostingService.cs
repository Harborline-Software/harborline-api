using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// Posts pay runs to the GL in v1 manual-entry mode. The operator supplies
/// all amounts on each <see cref="PayRunLine"/>; this service maps them to
/// balanced double-entry journal entries and persists both the updated
/// <see cref="PayRun"/> and the GL entry.
/// </summary>
/// <remarks>
/// v1 scope: manual-entry only. No automated payroll calculation, no
/// jurisdiction tax engine. Those are v2 category-providers.
/// </remarks>
public interface IPayRunPostingService
{
    /// <summary>
    /// Post a Draft pay run to the GL. Transitions the run to
    /// <see cref="PayRunStatus.Posted"/> and persists a balanced journal entry.
    /// Idempotent: if the run is already Posted the existing JE id is returned
    /// without re-posting.
    /// </summary>
    Task<PostPayRunResult> PostAsync(
        PayRunId payRunId,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse a Posted pay run. Transitions the run to
    /// <see cref="PayRunStatus.Reversed"/> and posts a mirror reversal entry.
    /// Both the original and the reversal remain in the GL
    /// (reverse-not-delete invariant).
    /// </summary>
    Task<ReversePayRunResult> ReverseAsync(
        PayRunId payRunId,
        string reason,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IPayRunPostingService.PostAsync"/>.</summary>
public sealed record PostPayRunResult(
    PayRun? PayRun,
    JournalEntryId? PostedEntryId,
    PostPayRunError Error,
    string? Detail)
{
    /// <summary>True iff the operation succeeded.</summary>
    public bool IsSuccess => Error == PostPayRunError.None;
}

/// <summary>Outcome of <see cref="IPayRunPostingService.ReverseAsync"/>.</summary>
public sealed record ReversePayRunResult(
    PayRun? PayRun,
    JournalEntryId? ReversalEntryId,
    ReversePayRunError Error,
    string? Detail)
{
    /// <summary>True iff the operation succeeded.</summary>
    public bool IsSuccess => Error == ReversePayRunError.None;
}

/// <summary>Structured failure modes for <see cref="IPayRunPostingService.PostAsync"/>.</summary>
public enum PostPayRunError
{
    /// <summary>Success.</summary>
    None,
    /// <summary>The pay run id does not resolve to a live pay run.</summary>
    UnknownPayRun,
    /// <summary>The pay run is not in <see cref="PayRunStatus.Draft"/>.</summary>
    InvalidStatus,
    /// <summary>The pay run has no lines; cannot post an empty run.</summary>
    NoLines,
    /// <summary>
    /// One or more employee ids in the pay run lines do not resolve to
    /// active employees in this tenant.
    /// </summary>
    UnknownEmployee,
    /// <summary>The downstream <see cref="Harborline.Api.Blocks.FinancialLedger.Services.IJournalPostingService"/> rejected the entry.</summary>
    JournalRejected,
}

/// <summary>Structured failure modes for <see cref="IPayRunPostingService.ReverseAsync"/>.</summary>
public enum ReversePayRunError
{
    /// <summary>Success.</summary>
    None,
    /// <summary>The pay run id does not resolve.</summary>
    UnknownPayRun,
    /// <summary>The pay run is not in <see cref="PayRunStatus.Posted"/>.</summary>
    InvalidStatus,
    /// <summary>The pay run has no journal entry to reverse (corrupted state).</summary>
    NoJournalEntry,
    /// <summary>The downstream <see cref="Harborline.Api.Blocks.FinancialLedger.Services.IJournalPostingService"/> rejected the reversal.</summary>
    JournalRejected,
}
