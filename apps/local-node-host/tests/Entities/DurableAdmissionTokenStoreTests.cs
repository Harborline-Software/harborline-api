using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.LocalNodeHost.Data.Admission;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Acceptance coverage for the DURABLE admission-token store (cerebrum [2026-06-21] in-memory admission token
/// store follow-on). The headline property: a minted invite SURVIVES a node restart within its TTL — the
/// in-memory v1 lost invites on every host recycle. Proves:
/// <list type="number">
///   <item><b>RESTART SURVIVAL</b> — an invite minted by one store instance is redeemable by a SECOND store
///     instance opened over the SAME database file (the restart simulation), within its TTL;</item>
///   <item>single-use is durable — a redeemed invite is rejected (AlreadyRedeemed) by a post-restart store;</item>
///   <item>TTL is durable — an invite past its TTL is rejected (Expired) by a post-restart store;</item>
///   <item>unknown ids fail closed (UnknownToken);</item>
///   <item>the redeemed token carries the persisted (public) team trust anchor verbatim.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Restart simulation = a NEW DbContext factory over the SAME file.</b> Mirrors
/// <c>RosterCrdtConvergenceTests</c>: the durable store is exercised over a plain file-backed SQLite store (the
/// SQLCipher encryption is covered separately by the Encryption test suite — this suite tests the store's
/// persistence + single-use + TTL logic). Disposing the first factory's provider (the connection string is
/// Pooling=False, so disposal fully releases the file handle) then opening a fresh factory over the same
/// path is exactly the "process recycled, file persists" case.
/// </remarks>
public sealed class DurableAdmissionTokenStoreTests : IAsyncLifetime
{
    private string _dir = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"harborline-admission-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _connectionString = $"Data Source={Path.Combine(_dir, "admission.db")};Pooling=False";

        // Create the schema once up front (the production path migrates; EnsureCreated is the test-store
        // equivalent and mirrors the roster convergence tests).
        await using var bootSp = BuildProvider();
        var factory = bootSp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalAdmissionDbContext>(opt => opt.UseSqlite(_connectionString));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A fresh store over a fresh provider — opening one of these AFTER disposing a prior one (and clearing the
    /// SQLite pools) simulates a node restart against the persisted file.
    /// </summary>
    private (ServiceProvider Sp, DurableAdmissionTokenStore Store) NewStore()
    {
        var sp = BuildProvider();
        var store = new DurableAdmissionTokenStore(
            sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>());
        return (sp, store);
    }

    private static TeamTrustAnchor SampleAnchor() => new(
        TeamId: Guid.Parse("7e57aaaa-0000-0000-0000-000000000001").ToString("D"),
        GenesisPartyId: "os:operator#deadbeef",
        GenesisPublicKey: "Zm9vYmFyLWdlbmVzaXMta2V5");

    [Fact]
    public async Task Minted_invite_survives_a_restart_and_is_redeemable_within_TTL()
    {
        var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
        var anchor = SampleAnchor();
        AdmissionToken minted;

        // ── BOOT 1: mint + issue, then "shut down" (dispose the provider — Pooling=False releases the file). ──
        var (sp1, store1) = NewStore();
        try
        {
            minted = AdmissionToken.Mint(anchor, now, TimeSpan.FromMinutes(15));
            store1.Issue(minted);
        }
        finally
        {
            await sp1.DisposeAsync();
        }

        // ── BOOT 2: a brand-new store over the SAME file — the invite must still be redeemable. ──
        var (sp2, store2) = NewStore();
        try
        {
            // Still within TTL (5 minutes after mint).
            var result = store2.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(5));

            Assert.True(result.Accepted, "a minted invite must survive a restart and redeem within TTL");
            Assert.Equal(RedeemOutcome.Accepted, result.Outcome);
            Assert.NotNull(result.Token);
            // The persisted (public) anchor round-trips verbatim.
            Assert.Equal(anchor.TeamId, result.Token!.Anchor.TeamId);
            Assert.Equal(anchor.GenesisPartyId, result.Token.Anchor.GenesisPartyId);
            Assert.Equal(anchor.GenesisPublicKey, result.Token.Anchor.GenesisPublicKey);
            Assert.Equal(minted.TokenId, result.Token.TokenId);
        }
        finally
        {
            await sp2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Single_use_is_durable_a_redeemed_invite_is_rejected_after_restart()
    {
        var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
        AdmissionToken minted;

        // BOOT 1: mint, issue, and REDEEM once.
        var (sp1, store1) = NewStore();
        try
        {
            minted = AdmissionToken.Mint(SampleAnchor(), now, TimeSpan.FromMinutes(15));
            store1.Issue(minted);
            var first = store1.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(1));
            Assert.True(first.Accepted);
        }
        finally
        {
            await sp1.DisposeAsync();
        }

        // BOOT 2: a replay after restart must be rejected as AlreadyRedeemed (not Unknown — the row persists).
        var (sp2, store2) = NewStore();
        try
        {
            var replay = store2.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(2));
            Assert.False(replay.Accepted);
            Assert.Equal(RedeemOutcome.AlreadyRedeemed, replay.Outcome);
            Assert.Null(replay.Token);
        }
        finally
        {
            await sp2.DisposeAsync();
        }
    }

    [Fact]
    public async Task TTL_is_durable_an_expired_invite_is_rejected_after_restart()
    {
        var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
        AdmissionToken minted;

        // BOOT 1: mint a short-TTL invite, issue it, do NOT redeem.
        var (sp1, store1) = NewStore();
        try
        {
            minted = AdmissionToken.Mint(SampleAnchor(), now, TimeSpan.FromMinutes(15));
            store1.Issue(minted);
        }
        finally
        {
            await sp1.DisposeAsync();
        }

        // BOOT 2: redeem AFTER the TTL window — must be rejected as Expired (fail-closed).
        var (sp2, store2) = NewStore();
        try
        {
            var expired = store2.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(20));
            Assert.False(expired.Accepted);
            Assert.Equal(RedeemOutcome.Expired, expired.Outcome);
            Assert.Null(expired.Token);
        }
        finally
        {
            await sp2.DisposeAsync();
        }
    }

    [Fact]
    public void Unknown_token_id_fails_closed()
    {
        var (sp, store) = NewStore();
        try
        {
            var result = store.Redeem("never-minted-id", DateTimeOffset.UtcNow);
            Assert.False(result.Accepted);
            Assert.Equal(RedeemOutcome.UnknownToken, result.Outcome);
            Assert.Null(result.Token);
        }
        finally
        {
            sp.Dispose();
        }
    }

    [Fact]
    public void Single_use_is_enforced_in_process_a_second_redeem_is_replayed()
    {
        var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
        var (sp, store) = NewStore();
        try
        {
            var minted = AdmissionToken.Mint(SampleAnchor(), now, TimeSpan.FromMinutes(15));
            store.Issue(minted);

            var first = store.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(1));
            Assert.True(first.Accepted);

            var second = store.Redeem(minted.TokenId, now + TimeSpan.FromMinutes(2));
            Assert.False(second.Accepted);
            Assert.Equal(RedeemOutcome.AlreadyRedeemed, second.Outcome);
        }
        finally
        {
            sp.Dispose();
        }
    }
}
