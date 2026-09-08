using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Feed;

/// <summary>
/// Mock tier-2 <see cref="IBankFeedProvider"/> for development and testing.
/// Per ADR 0096 Mock-first discipline + ADR 0112 Part 2.
/// </summary>
/// <remarks>
/// <para>
/// Carries the <see cref="IMockVendorProvider"/> marker so
/// <c>MockProviderProductionGuardAssertion</c> (ADR 0096 §D1c) blocks this
/// mock from running in a production environment unless the global opt-out
/// env var <c>HARBORLINE_ALLOW_MOCK_PROVIDERS=true</c> is set.
/// </para>
/// <para>
/// Returns deterministic in-memory data. The mock credential blob is a
/// plaintext sentinel string — real adapters MUST use an encrypted-at-rest
/// vault (ADR 0112 sec-eng C1). The mock never logs credentials or
/// sensitive data.
/// </para>
/// <para>
/// <strong>NOT a real feed implementation.</strong> Used to satisfy the DI
/// contract during development, demos, and unit tests without requiring a
/// live provider credential.
/// </para>
/// </remarks>
public sealed class MockBankFeedProvider : IBankFeedProvider, IMockVendorProvider
{
    private const string MockConnectionId = "mock-connection-001";
    private const string MockCredentialBlob = "mock::sentinel::not-a-real-credential";
    private readonly TimeProvider _time;

    /// <summary>Creates the deterministic mock over the host clock.</summary>
    public MockBankFeedProvider(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public RefreshMode RefreshMode => RefreshMode.PollOnly;

    /// <inheritdoc />
    public Task<ConnectionStart> BeginConnectAsync(CancellationToken ct = default)
        => Task.FromResult<ConnectionStart>(
            new ConnectionStart.RedirectFlow("https://mock.bank.example/connect"));

    /// <inheritdoc />
    public Task<BankFeedConnection> CompleteConnectAsync(
        string callbackOrClaim, CancellationToken ct = default)
        => Task.FromResult(new BankFeedConnection(
            ConnectionId: MockConnectionId,
            ProviderName: "MockBankFeed",
            EncryptedCredentialBlob: MockCredentialBlob,
            Status: ConnectionStatus.Healthy));

    /// <inheritdoc />
    public Task<ConnectionStatus> GetConnectionStatusAsync(
        BankFeedConnection connection, CancellationToken ct = default)
        => Task.FromResult(ConnectionStatus.Healthy);

    /// <inheritdoc />
    public Task<IReadOnlyList<BankFeedAccount>> ListAccountsAsync(
        BankFeedConnection connection, CancellationToken ct = default)
    {
        IReadOnlyList<BankFeedAccount> accounts = new List<BankFeedAccount>
        {
            new(
                ProviderAccountId: "mock-chk-001",
                DisplayName: "Mock Checking",
                InstitutionName: "Mock Bank",
                Currency: new CurrencyCode("USD"),
                Type: BankFeedAccountType.Checking,
                Mask: "1234"),
        };
        return Task.FromResult(accounts);
    }

    /// <inheritdoc />
    public Task<TransactionPage> PullTransactionsAsync(
        BankFeedConnection connection,
        BankFeedAccount account,
        string? cursor,
        CancellationToken ct = default)
    {
        // Return an empty page with a stable synthesized cursor so callers can
        // progress through the incremental sync loop without hanging.
        var nextCursor = cursor is null ? "mock-cursor-v1-epoch" : null;
        return Task.FromResult(new TransactionPage(
            Transactions: Array.Empty<BankFeedTransaction>(),
            NextCursor: nextCursor));
    }

    /// <inheritdoc />
    public Task<BankFeedBalance> GetBalanceAsync(
        BankFeedConnection connection,
        BankFeedAccount account,
        CancellationToken ct = default)
        => Task.FromResult(new BankFeedBalance(
            Current: 0m,
            Available: 0m,
            AsOf: new Instant(_time.GetUtcNow())));

    /// <inheritdoc />
    public Task DisconnectAsync(BankFeedConnection connection, CancellationToken ct = default)
    {
        // Mock: credential is already a sentinel — nothing to shred.
        // Real adapters MUST call keystore.DeleteKeyAsync + overwrite here
        // before best-effort upstream revoke (ADR 0112 sec-eng C5).
        return Task.CompletedTask;
    }
}
