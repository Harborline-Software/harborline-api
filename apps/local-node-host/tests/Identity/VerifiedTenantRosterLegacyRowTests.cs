using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class VerifiedTenantRosterLegacyRowTests : IAsyncLifetime
{
    private static readonly Guid Team = Guid.Parse("ad030000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private string _directory = null!;
    private TestRosterContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"legacy-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _factory = new TestRosterContextFactory($"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False");
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public async Task ReadAsync_RefusesLegacyRowAtAndBeyondReceiptWindow(int issuedAhead)
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        var roster = MemberRoster.Genesis(Team, "founder", founder, Verifier, IssuedAt, Guid.NewGuid())
            .Admit("founder", founder, "member", KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member, Verifier, IssuedAt.AddSeconds(issuedAhead), Guid.NewGuid());
        var rows = roster.EnumerateAdmissions().Select(admission => NodeRosterRecord.FromCrdtState(
            RosterRecordCrdtState.FromAdmission(admission).AttestReceipt(founder, "founder", IssuedAt))).ToArray();
        var legacy = rows.Single(row => row.PartyId == "member");
        legacy.WireFormatVersion = 3;
        await using (var db = _factory.CreateDbContext())
        {
            db.RosterRecords.AddRange(rows.Where(row => row.IsGenesis));
            await db.SaveChangesAsync();
        }
        var reader = new VerifiedTenantRosterReader(_factory, Verifier);
        var tenant = new TenantId(Team.ToString("D"));
        var admittedAt = AdmittedInstant.FromRecordedAct(IssuedAt.AddSeconds(30));
        var before = await reader.ReadAsync(tenant, admittedAt, CancellationToken.None);
        Assert.True(before.Contains("founder"));
        Assert.False(before.Contains("member"));

        await using (var db = _factory.CreateDbContext())
        {
            db.RosterRecords.Add(legacy);
            await db.SaveChangesAsync();
        }
        var refused = await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(
            () => reader.ReadAsync(tenant, admittedAt, CancellationToken.None));
        Assert.Equal("WireVersionUnsupported", refused.Refusal.ToString());
        Assert.True(before.Contains("founder"));
        Assert.False(before.Contains("member"));

        await using (var db = _factory.CreateDbContext())
        {
            db.RosterRecords.Remove(await db.RosterRecords.SingleAsync(row => row.PartyId == "member"));
            await db.SaveChangesAsync();
        }
        var after = await reader.ReadAsync(tenant, admittedAt, CancellationToken.None);
        Assert.True(after.Contains("founder"));
        Assert.False(after.Contains("member"));
        Assert.Equal(before.Members.Select(member => member.PartyId).Order(),
            after.Members.Select(member => member.PartyId).Order());
    }

    private sealed class TestRosterContextFactory(string connectionString) : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NodeLocalRosterDbContext>().UseSqlite(connectionString).Options);
    }
}
