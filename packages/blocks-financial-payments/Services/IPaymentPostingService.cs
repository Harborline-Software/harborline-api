using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

// ──────────────────────────────────────────────────────────────────────────────
// Result / error types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Error discriminant for <see cref="IPaymentPostingService.ClearAsync"/>.</summary>
public enum ClearError
{
    None = 0,
    UnknownPayment,
    InvalidStatusForClear,
    JournalRejected,
}

/// <summary>Error discriminant for <see cref="IPaymentPostingService.BounceAsync"/>.</summary>
public enum BounceError
{
    None = 0,
    UnknownPayment,
    InvalidStatusForBounce,

    /// <summary>
    /// The reversal journal entry was refused. This is also how the fiscal-period gate reaches the
    /// caller: the entry is dated today, so <c>JournalPostingService</c> phase 4 refuses it when the
    /// period covering the reversal date is missing, Locked, or SoftClosed without the override permission.
    /// The detail carries which.
    /// </summary>
    JournalRejected,

    /// <summary>
    /// A prior application could not be reversed, so its balance was deliberately left alone rather
    /// than restored without contra evidence. The bounce is INCOMPLETE — the reversal journal entry
    /// is already posted and earlier applications in the batch are already reversed — so the message
    /// names the application that stopped it and the payment is left un-bounced for a human to
    /// resolve.
    /// </summary>
    ReversalNotRecorded,
}

/// <summary>Error discriminant for <see cref="IPaymentPostingService.VoidAsync"/>.</summary>
public enum VoidError
{
    None = 0,
    UnknownPayment,
    InvalidStatusForVoid,
}

/// <summary>Result of <see cref="IPaymentPostingService.ClearAsync"/>.</summary>
public sealed record ClearResult(Payment? Payment, JournalEntryId? JournalEntryId, ClearError Error, string? ErrorMessage);

/// <summary>Result of <see cref="IPaymentPostingService.BounceAsync"/>.</summary>
public sealed record BounceResult(Payment? Payment, JournalEntryId? JournalEntryId, BounceError Error, string? ErrorMessage);

/// <summary>Result of <see cref="IPaymentPostingService.VoidAsync"/>.</summary>
public sealed record VoidResult(Payment? Payment, VoidError Error, string? ErrorMessage);

// ──────────────────────────────────────────────────────────────────────────────
// Service interface (stub — implemented in PR 2)
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Posting service that transitions <see cref="Payment"/> status and writes
/// double-entry GL journal entries via <c>IJournalPostingService</c>.
///
/// <para>
/// <b>Declared here (PR 1) so downstream can reference the interface at compile
/// time.</b> <c>DefaultPaymentPostingService</c> ships in PR 2.
/// </para>
///
/// <para>
/// GL entries per direction:
/// <list type="bullet">
///   <item>Inbound ClearAsync: Dr BankAccountId / Cr AR control account.</item>
///   <item>Outbound ClearAsync: Dr AP control account / Cr BankAccountId.</item>
///   <item>BounceAsync: reversal JE (flipped debits/credits) + immutable reversal of all prior applications.</item>
/// </list>
/// </para>
/// </summary>
public interface IPaymentPostingService
{
    /// <summary>
    /// Transition <see cref="PaymentStatus.Draft"/> → <see cref="PaymentStatus.Unapplied"/> (or
    /// <see cref="PaymentStatus.PartiallyApplied"/> if applications already exist).
    /// Posts the clearing GL entry. Idempotent: already-Cleared returns the existing
    /// <see cref="JournalEntryId"/>.
    /// </summary>
    Task<ClearResult> ClearAsync(PaymentId id, Harborline.Api.Foundation.Authorization.AuthorizationWriteContext authority, CancellationToken ct = default);

    /// <summary>
    /// Transition Cleared/PartiallyApplied/Unapplied → <see cref="PaymentStatus.Bounced"/>.
    /// Posts reversing JE; for each prior <see cref="PaymentApplication"/> restores
    /// Invoice/Bill balance and retains linked contra-application evidence.
    ///
    /// <para>
    /// <b>Note:</b> BounceAsync is the only place in the cluster where the posting
    /// service touches invoice/bill repositories directly (to restore balances after
    /// reversal). This is intentional — the bounce path must be atomic from the
    /// caller's perspective.
    /// </para>
    /// </summary>
    Task<BounceResult> BounceAsync(PaymentId id, string reason, Harborline.Api.Foundation.Authorization.AuthorizationWriteContext authority, CancellationToken ct = default);

    /// <summary>
    /// Transition <see cref="PaymentStatus.Draft"/> → <see cref="PaymentStatus.Voided"/>.
    /// No GL entry. Fails if the payment has already been cleared.
    /// </summary>
    Task<VoidResult> VoidAsync(PaymentId id, string reason, Harborline.Api.Foundation.Authorization.AuthorizationWriteContext authority, CancellationToken ct = default);
}
