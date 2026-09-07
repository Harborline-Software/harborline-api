using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Feed;

/// <summary>
/// Tier-2 category-provider seam for live bank-feed connectors.
/// Per ADR 0112 Part 2 — IBankFeedProvider seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong>AIS (account information) ONLY — read-only by construction.</strong>
/// There is no payment-initiation (PIS) member. Adding a write/transfer member
/// is an ADR amendment, not an adapter detail (ADR 0112 §Privacy invariants 5).
/// </para>
/// <para>
/// <strong>The seam is the only sanctioned consented data-egress exception to the
/// local-first doctrine.</strong> Every adapter behind this seam MUST:
/// <list type="bullet">
///   <item><description>Not introduce a Harborline-hosted proxy (operator-hardware → aggregator, direct).</description></item>
///   <item><description>Store credentials as opaque sealed blobs (never plaintext on disk).</description></item>
///   <item><description>Never log credentials, tokens, or Authorization header values.</description></item>
///   <item><description>Cryptographically shred credentials on <see cref="DisconnectAsync"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Cursor contract (mandatory even for poll-only providers):</strong>
/// poll-only adapters (e.g. SimpleFIN) synthesize a cursor from a
/// posted-since watermark + seen-id set. This keeps incremental sync uniform
/// across provider shapes. Adapters that receive a cursor MUST pass it through;
/// adapters without a native cursor MUST synthesize one.
/// </para>
/// <para>
/// <strong>v1 / v2 cut:</strong> only <see cref="MockBankFeedProvider"/> ships
/// in v1. Real adapters (SimpleFIN, Teller, Enable Banking, Basiq) are v2.
/// </para>
/// </remarks>
public interface IBankFeedProvider
{
    /// <summary>
    /// Whether this provider supports webhook-driven notifications (optimization)
    /// or is poll-only.
    /// </summary>
    RefreshMode RefreshMode { get; }

    // --- Connection lifecycle ---

    /// <summary>
    /// Begins a new feed connection. Returns either an OAuth redirect URL or a
    /// claim URL depending on the provider type.
    /// </summary>
    Task<ConnectionStart> BeginConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Completes a connection after the operator has completed the provider flow
    /// (OAuth callback or SimpleFIN token claim). Returns a durable connection
    /// record with an opaque, provider-owned, encrypted-at-rest credential blob.
    /// </summary>
    Task<BankFeedConnection> CompleteConnectAsync(string callbackOrClaim, CancellationToken ct = default);

    /// <summary>Returns the current health of a connection.</summary>
    Task<ConnectionStatus> GetConnectionStatusAsync(BankFeedConnection connection, CancellationToken ct = default);

    // --- Account discovery ---

    /// <summary>Returns the list of accounts accessible via this connection.</summary>
    Task<IReadOnlyList<BankFeedAccount>> ListAccountsAsync(BankFeedConnection connection, CancellationToken ct = default);

    // --- Transaction pull with cursor ---

    /// <summary>
    /// Pulls a page of transactions for an account. The cursor is mandatory;
    /// pass null for the first pull (returns from the provider's earliest available
    /// date or the account's cutover date). Each call returns the next cursor
    /// for incremental sync.
    /// </summary>
    Task<TransactionPage> PullTransactionsAsync(
        BankFeedConnection connection,
        BankFeedAccount account,
        string? cursor,
        CancellationToken ct = default);

    // --- Balance ---

    /// <summary>Returns the current point-in-time balance for an account.</summary>
    Task<BankFeedBalance> GetBalanceAsync(BankFeedConnection connection, BankFeedAccount account, CancellationToken ct = default);

    // --- Disconnect ---

    /// <summary>
    /// Revokes the connection and cryptographically shreds the locally-stored credential.
    /// Local shred happens first, then best-effort upstream revoke.
    /// If upstream revoke fails, local teardown still completes and status reflects Revoked/Error.
    /// Per ADR 0112 §Privacy invariants 6 (sec-eng C5).
    /// </summary>
    Task DisconnectAsync(BankFeedConnection connection, CancellationToken ct = default);
}
