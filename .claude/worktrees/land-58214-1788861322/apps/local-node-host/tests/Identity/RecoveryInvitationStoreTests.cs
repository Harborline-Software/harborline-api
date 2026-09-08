using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The MTW-2 #3013 recovery-invitation store: single-use consume across restart, F2 resume of a
/// consumed-but-incomplete recovery on the same credential commitment (never re-consuming), a
/// changed-credential resume refused as a changed-replay, replay-after-completion refused, and
/// unconsumed revoke. Structural purpose-binding is inherent — the recovery digest lives only in
/// the recovery table, so the account-setup path can never consume it.
/// </summary>
public sealed class RecoveryInvitationStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 5, 0, 0, TimeSpan.Zero);

    private static readonly string ArgonHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);

    [Fact]
    [Trait("PlanCard", "MTW-2-3013")]
    public async Task Recovery_Code_Consumes_Once_Then_Resumes_Then_Refuses_On_Change_And_Replay()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var issue = await fixture.Store.IssueAsync(Seed(fixture.AccountId, "cmd-1"));
        Assert.NotNull(issue);

        var commitmentX = "commitment-x";
        var commitmentY = "commitment-y";

        // Fresh single-use consume returns the target pins.
        var first = await fixture.Store.BeginOrResumeAsync(issue!.RawCode, commitmentX, Now);
        Assert.Equal(RecoveryConsumeStatus.Consumed, first.Status);
        Assert.Equal(fixture.AccountId, first.TargetAccountId);
        Assert.Equal("FOUNDER", first.TargetNormalizedUsername);

        // Same code + same commitment → resume WITHOUT re-consuming (F2).
        var resumed = await fixture.Store.BeginOrResumeAsync(issue.RawCode, commitmentX, Now.AddMinutes(1));
        Assert.Equal(RecoveryConsumeStatus.Resumed, resumed.Status);
        Assert.Equal(fixture.AccountId, resumed.TargetAccountId);

        // Same code + DIFFERENT commitment → changed-replay.
        var changed = await fixture.Store.BeginOrResumeAsync(issue.RawCode, commitmentY, Now.AddMinutes(2));
        Assert.Equal(RecoveryConsumeStatus.ChangedReplay, changed.Status);

        // After completion, any replay of the code is refused.
        await fixture.Store.MarkCompletedAsync(first.RecoveryInvitationId!, Now.AddMinutes(3));
        var afterComplete = await fixture.Store.BeginOrResumeAsync(issue.RawCode, commitmentX, Now.AddMinutes(4));
        Assert.Equal(RecoveryConsumeStatus.Refused, afterComplete.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3013")]
    public async Task Unknown_Expired_And_Revoked_Codes_Refuse()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        // Unknown code.
        var unknown = await fixture.Store.BeginOrResumeAsync(
            "unknown-code-with-fixture-entropy-1234567890", "commitment", Now);
        Assert.Equal(RecoveryConsumeStatus.Refused, unknown.Status);

        // Expired code.
        var expired = await fixture.Store.IssueAsync(Seed(fixture.AccountId, "cmd-expired"));
        Assert.NotNull(expired);
        var expiredResult = await fixture.Store.BeginOrResumeAsync(
            expired!.RawCode, "commitment", Now.AddHours(2));
        Assert.Equal(RecoveryConsumeStatus.Refused, expiredResult.Status);

        // Revoked (unconsumed) code.
        var revocable = await fixture.Store.IssueAsync(Seed(fixture.AccountId, "cmd-revoke"));
        Assert.NotNull(revocable);
        Assert.True(await fixture.Store.RevokeAsync(
            revocable!.RecoveryInvitationId, revocable.TenantId, Now.AddMinutes(1)));
        var revokedResult = await fixture.Store.BeginOrResumeAsync(
            revocable.RawCode, "commitment", Now.AddMinutes(2));
        Assert.Equal(RecoveryConsumeStatus.Refused, revokedResult.Status);
    }

    private static RecoveryInvitationSeed Seed(string accountId, string fingerprint) =>
        new(
            TenantId: Guid.NewGuid().ToString("D"),
            IssuerAccountId: "admin-account",
            IssuerPrincipalId: "admin-principal",
            TargetAccountId: accountId,
            TargetNormalizedUsername: "FOUNDER",
            CommandFingerprint: Digest(fingerprint),
            IssuedAtUtc: Now.AddMinutes(-1),
            AbsoluteExpiresAtUtc: Now.AddHours(1));

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private StoreFixture(string directory, RecoveryInvitationStore store, string accountId)
        {
            _directory = directory;
            Store = store;
            AccountId = accountId;
        }

        public RecoveryInvitationStore Store { get; }

        public string AccountId { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"recovery-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new IdentityContextFactory(Path.Combine(directory, "identity.db"));
            await using (var identity = factory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }

            var time = new FixedTimeProvider(Now);
            var bootstrap = new InstallationFounderBootstrapService(factory, time);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                ArgonHash,
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap"));

            string accountId;
            await using (var identity = factory.CreateDbContext())
            {
                accountId = (await identity.Accounts.AsNoTracking()
                    .SingleAsync(a => a.NormalizedUsername == "FOUNDER")).AccountId;
            }

            return new StoreFixture(directory, new RecoveryInvitationStore(factory), accountId);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
