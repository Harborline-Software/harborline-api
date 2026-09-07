using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Feed;

/// <summary>
/// Whether a feed provider supports webhook-driven push or is poll-only.
/// Per ADR 0112 Part 2 — RefreshMode capability flag.
/// </summary>
public enum RefreshMode
{
    /// <summary>Adapter polls the provider on a schedule (e.g. SimpleFIN ~once-daily).</summary>
    PollOnly,

    /// <summary>
    /// Provider can deliver webhook events (optimization). Adapters translate
    /// incoming webhooks into a <see cref="IBankFeedProvider.PullTransactionsAsync"/> call
    /// with the latest cursor — webhooks never become a core dependency.
    /// </summary>
    WebhookCapable,
}

/// <summary>
/// The result of <see cref="IBankFeedProvider.BeginConnectAsync"/>.
/// Either an OAuth redirect URL or a SimpleFIN-style token claim URL.
/// </summary>
public abstract record ConnectionStart
{
    /// <summary>Provider requires an OAuth browser redirect to complete authorization.</summary>
    public sealed record RedirectFlow(string Url) : ConnectionStart;

    /// <summary>Provider uses a one-time claim token (e.g. SimpleFIN setup token).</summary>
    public sealed record TokenClaimFlow(string ClaimUrl) : ConnectionStart;
}

/// <summary>
/// A persisted feed connection after <see cref="IBankFeedProvider.CompleteConnectAsync"/>.
/// The credential is an opaque sealed blob — only the adapter can interpret it.
/// Never log or expose the credential outside the adapter.
/// </summary>
/// <remarks>
/// <para>
/// <strong>EncryptedCredentialBlob:</strong> opaque, provider-owned, encrypted-at-rest
/// credential blob. The adapter is the only code that parses this value.
/// ADR 0112 sec-eng C1: must be stored in a named credential vault (never plaintext-on-disk).
/// </para>
/// </remarks>
public sealed record BankFeedConnection(
    string ConnectionId,
    string ProviderName,
    string EncryptedCredentialBlob,
    ConnectionStatus Status);

/// <summary>
/// The connection health status.
/// Per ADR 0112 Part 2 — GetConnectionStatus.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Connection is healthy; transactions can be pulled.</summary>
    Healthy,

    /// <summary>
    /// The connection needs re-authorization (e.g. PSD2 ~90-day consent expiry,
    /// revoked OAuth token). Operator must re-connect.
    /// </summary>
    NeedsReauth,

    /// <summary>Connection was revoked (upstream or by operator).</summary>
    Revoked,

    /// <summary>Provider-side error; pull may fail but connection is not necessarily invalid.</summary>
    Error,
}

/// <summary>
/// An account discovered via <see cref="IBankFeedProvider.ListAccountsAsync"/>.
/// </summary>
/// <remarks>
/// <strong>Mask:</strong> last 4 digits or account-number mask as reported by the provider; null if unavailable.
/// </remarks>
public sealed record BankFeedAccount(
    string ProviderAccountId,
    string DisplayName,
    string? InstitutionName,
    CurrencyCode Currency,
    BankFeedAccountType Type,
    string? Mask);

/// <summary>Account type as reported by the feed provider.</summary>
public enum BankFeedAccountType
{
    /// <summary>Standard transactional checking account.</summary>
    Checking,

    /// <summary>Savings account.</summary>
    Savings,

    /// <summary>Credit card account (statement balance is a liability).</summary>
    CreditCard,

    /// <summary>Loan or mortgage account.</summary>
    Loan,

    /// <summary>Brokerage / investment account.</summary>
    Investment,

    /// <summary>Any account type not covered by the above values.</summary>
    Other,
}

/// <summary>
/// A page of transactions returned by <see cref="IBankFeedProvider.PullTransactionsAsync"/>.
/// </summary>
/// <remarks>
/// <strong>NextCursor:</strong> opaque cursor for the next incremental pull; null when this is the last available page.
/// </remarks>
public sealed record TransactionPage(
    IReadOnlyList<BankFeedTransaction> Transactions,
    string? NextCursor);

/// <summary>
/// A single transaction as returned by a feed provider, before normalization
/// to <see cref="Harborline.Api.Blocks.Banking.Models.StatementLine"/>.
/// </summary>
/// <remarks>
/// <strong>Raw:</strong> opaque provider payload for re-derivation / debug.
/// Treated as a sealed/redacted field per ADR 0112 sec-eng C2 — never logged; bounded retention.
/// </remarks>
public sealed record BankFeedTransaction(
    string ProviderTxnId,
    Instant PostedAt,
    decimal Amount,
    CurrencyCode Currency,
    string Description,
    bool Pending,
    string? Raw);

/// <summary>
/// Point-in-time balance for an account.
/// Per ADR 0112 Part 2 — GetBalance.
/// </summary>
public sealed record BankFeedBalance(
    decimal Current,
    decimal? Available,
    Instant AsOf);
