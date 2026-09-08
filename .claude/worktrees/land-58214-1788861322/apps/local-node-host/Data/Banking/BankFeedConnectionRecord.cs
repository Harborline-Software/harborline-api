namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// Node-local record tracking whether a bank account has an active mock feed connection.
/// Mapped by <see cref="NodeLocalBankFeedDbContext"/> — NOT a shared
/// <see cref="Harborline.Api.Foundation.Persistence.IHarborlineEntityModule"/> — so the council C2
/// both-provider model-drift test (<c>BothProviderModelDriftTests</c>) never sees it.
/// </summary>
/// <remarks>
/// <para>
/// Feed-connection state is node-exclusive: the mock provider (<see cref="Harborline.Api.Blocks.Banking.Feed.MockBankFeedProvider"/>)
/// is stateless, so the <em>fact</em> that an operator connected it for an account must be
/// persisted locally to survive process restarts. A real AIS adapter (SimpleFIN/Teller) would
/// hold its own durable credential blob in the node store; this table serves the same role for
/// the mock (no credential — just the connected timestamp).
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> The record lives in the SAME SQLCipher-encrypted database
/// file as the financial store, keyed through the same
/// <see cref="SqlCipherConnectionInterceptor"/> on the connection — there is no plaintext path.
/// </para>
/// <para>
/// One row per account: a row's existence means <c>feedConnected=true</c>; absence means
/// <c>feedConnected=false</c>. The PK is the <c>BankAccountId.Value</c> string.
/// </para>
/// </remarks>
public sealed class BankFeedConnectionRecord
{
    /// <summary>
    /// The bank account's id (<see cref="Harborline.Api.Blocks.Banking.Models.BankAccountId.Value"/>).
    /// Primary key. One row per connected account.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>UTC timestamp of when the feed was connected.</summary>
    public DateTimeOffset ConnectedAt { get; set; }
}
