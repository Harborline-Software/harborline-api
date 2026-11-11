using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Roster;

using RuntimeTeamId = Harborline.Api.Kernel.Runtime.Teams.TeamId;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class VerifiedTenantRosterReaderTests : IAsyncLifetime
{
    private static readonly Guid Team = Guid.Parse("ad030000-0000-0000-0000-000000000003");
    private static readonly Guid OtherTeam = Guid.Parse("ad030000-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private string _directory = null!;
    private string _connectionString = null!;
    private TestRosterContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"adm03-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False";
        _factory = new TestRosterContextFactory(_connectionString);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_RefusesSignedProvenanceSubstitutionAsTampered(bool genesis)
    {
        var fixture = BuildRoster();
        var rows = fixture.Admissions.Select(admission => ToRow(admission, fixture)).ToArray();
        rows.Single(row => row.IsGenesis == genesis).MintingSessionEvidence = "tampered";
        await SeedAsync(rows);
        await AssertRefusalAsync(VerifiedTenantRosterRefusal.Tampered, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RebuildsVerifiedRoster_AfterStoreRestart()
    {
        var fixture = BuildRoster();
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture)));

        var beforeRestart = await NewReader().ReadAsync(Tenant(), AdmittedInstant.FromRecordedAct(IssuedAt), CancellationToken.None);
        Assert.True(beforeRestart.Contains(fixture.MemberParty));
        Assert.True(beforeRestart.ValidatesToGenesis(Verifier));

        SqliteConnection.ClearAllPools();
        var restartedFactory = new TestRosterContextFactory(_connectionString);
        var afterRestart = await new VerifiedTenantRosterReader(restartedFactory, Verifier)
            .ReadAsync(Tenant(), AdmittedInstant.FromRecordedAct(IssuedAt), CancellationToken.None);

        Assert.Equal(Team, afterRestart.TeamId);
        Assert.Equal(beforeRestart.Members.Select(member => member.PartyId).Order(),
            afterRestart.Members.Select(member => member.PartyId).Order());
        Assert.True(afterRestart.ValidatesToGenesis(Verifier));
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesTamperedSignature()
    {
        var fixture = BuildRoster();
        var rows = fixture.Admissions.Select(admission => ToRow(admission, fixture)).ToArray();
        rows.Single(row => row.PartyId == fixture.MemberParty).SignatureB64Url = "not-a-signature";
        await SeedAsync(rows);

        await AssertRefusalAsync(VerifiedTenantRosterRefusal.Tampered, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_ValidRevocationRefusesRevokedPartyTrust()
    {
        var fixture = BuildRoster();
        var (_, revocation) = fixture.Roster.SignRevoke(
            fixture.FounderParty,
            fixture.FounderSigner,
            fixture.MemberParty,
            Verifier,
            IssuedAt.AddMinutes(2),
            Guid.Parse("ad030000-0000-0000-0000-000000000023"));
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture))
            .Append(ToRow(revocation, fixture)));

        var verified = await NewReader().ReadAsync(Tenant(), AdmittedInstant.FromRecordedAct(IssuedAt), CancellationToken.None);

        Assert.True(verified.Contains(fixture.FounderParty));
        Assert.False(verified.Contains(fixture.MemberParty));
        Assert.Null(verified.PublicKeyOf(fixture.MemberParty));
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesOrphanAdmission()
    {
        var fixture = BuildRoster();
        var orphanSigner = new Ed25519Signer(KeyPair.Generate());
        var orphanMember = KeyPair.Generate();
        var orphanAdmission = RosterSigning.SignAdmission(
            orphanSigner,
            Team,
            "party:orphan-child",
            orphanMember.PrincipalId,
            "party:unreachable-admitter",
            isGenesis: false,
            IssuedAt.AddMinutes(3),
            Guid.Parse("ad030000-0000-0000-0000-000000000033"));
        var orphan = new MemberAdmissionRecord(
            Team.ToString("D"),
            "party:orphan-child",
            orphanMember.PrincipalId,
            orphanAdmission);
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture))
            .Append(ToRow(orphan, fixture)));

        await AssertRefusalAsync(VerifiedTenantRosterRefusal.Orphan, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesDuplicateValidAdmissionUnderAnotherDurableId()
    {
        var fixture = BuildRoster();
        var rows = fixture.Admissions.Select(admission => ToRow(admission, fixture)).ToList();
        var duplicate = RosterRecordCrdtState.FromAdmission(
            fixture.Admissions.Single(admission => admission.PartyId == fixture.MemberParty));
        duplicate = (duplicate with { RecordId = $"{duplicate.RecordId}:duplicate" })
            .AttestReceipt(fixture.FounderSigner, fixture.FounderParty,
                DateTimeOffset.Parse(duplicate.IssuedAtIso));
        rows.Add(NodeRosterRecord.FromCrdtState(duplicate));
        await SeedAsync(rows);

        await AssertRefusalAsync(VerifiedTenantRosterRefusal.Orphan, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesMissingGenesis()
    {
        var fixture = BuildRoster();
        await SeedAsync(fixture.Admissions
            .Where(admission => !admission.Admission.IsGenesis)
            .Select(admission => ToRow(admission, fixture)));

        await AssertRefusalAsync(VerifiedTenantRosterRefusal.MissingGenesis, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesMultipleGenesisAdmissions()
    {
        var fixture = BuildRoster();
        var secondFounder = new Ed25519Signer(KeyPair.Generate());
        var secondGenesis = MemberRoster.Genesis(
                Team,
                "party:second-founder",
                secondFounder,
                Verifier,
                IssuedAt.AddMinutes(4),
                Guid.Parse("ad030000-0000-0000-0000-000000000043"))
            .EnumerateAdmissions()
            .Single();
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture))
            .Append(ToRow(secondGenesis, fixture)));

        await AssertRefusalAsync(VerifiedTenantRosterRefusal.MultipleGenesis, Tenant());
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_RefusesWrongTenant()
    {
        var fixture = BuildRoster();
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture)));

        await AssertRefusalAsync(
            VerifiedTenantRosterRefusal.WrongTenant,
            new TenantId(OtherTeam.ToString("D")));
    }

    [Fact]
    [Trait("PlanCard", "ADM-03")]
    public async Task ReadAsync_OneHundredActiveTeamFlips_DoNotAffectExplicitTenantResult()
    {
        var fixture = BuildRoster();
        await SeedAsync(fixture.Admissions.Select(admission => ToRow(admission, fixture)));
        var reader = NewReader();
        var activeTeam = new MutableActiveTeamAccessor(new RuntimeTeamId(OtherTeam));

        for (var i = 0; i < 100; i++)
        {
            var pendingRead = reader.ReadAsync(Tenant(), AdmittedInstant.FromRecordedAct(IssuedAt), CancellationToken.None);
            await activeTeam.SetActiveAsync(
                new RuntimeTeamId(i % 2 == 0 ? Team : OtherTeam),
                CancellationToken.None);
            var verified = await pendingRead;

            Assert.Equal(Team, verified.TeamId);
            Assert.True(verified.Contains(fixture.MemberParty));
        }
    }

    private VerifiedTenantRosterReader NewReader() => new(_factory, Verifier);

    [Fact]
    public async Task StartupRead_AdmitsOneKernelClockInstantForGenesisProof()
    {
        var factory = new TestRosterContextFactory(
            $"Data Source={Path.Combine(_directory, "startup.db")};Pooling=False");
        var fixture = BuildRoster();
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.RosterRecords.Add(ToRow(fixture.Admissions.Single(admission => admission.Admission.IsGenesis), fixture));
            await db.SaveChangesAsync();
        }
        var clock = new CountingClock();

        var roster = await DurableGenesisIdentity.ReadAsync(factory, Team, fixture.FounderSigner.IssuerId,
            Verifier, clock, CancellationToken.None);

        Assert.NotNull(roster);
        Assert.True(roster.Contains(fixture.FounderParty));
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void ReadAsync_RequiresTheActsAdmittedInstant()
    {
        var read = Assert.Single(typeof(IVerifiedTenantRosterReader).GetMethods());
        Assert.Equal([typeof(TenantId), typeof(AdmittedInstant), typeof(CancellationToken)],
            read.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Theory]
    [Trait("HandMutant", "T-1056-cap-removal")]
    [InlineData(31, 31, false, false)]
    [InlineData(0, 60, false, false)]
    [InlineData(30, 30, true, false)]
    [InlineData(31, 31, false, true)]
    [InlineData(0, 60, false, true)]
    [InlineData(30, 30, true, true)]
    public async Task ReadAsync_UsesTheCallersAdmittedInstantForPlantedAndWindowEdgeAuthority(
        int issuedAhead, int receivedAhead, bool allowed, bool startup)
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        var admin = new Ed25519Signer(KeyPair.Generate());
        var permissions = PermissionSet.Of(Permission.MembersAdmit, TeamRolePermissions.RecordsRead);
        var roster = MemberRoster.Genesis(Team, "founder", founder, Verifier, IssuedAt, Guid.NewGuid())
            .Admit("founder", founder, "admin", admin.IssuerId, permissions, Verifier, IssuedAt, Guid.NewGuid())
            .Admit("admin", admin, "planted", KeyPair.Generate().PrincipalId,
                PermissionSet.Of(TeamRolePermissions.RecordsRead), Verifier,
                IssuedAt.AddSeconds(issuedAhead), Guid.NewGuid());
        await SeedAsync(roster.EnumerateAdmissions().Select(admission => NodeRosterRecord.FromCrdtState(
            RosterRecordCrdtState.FromAdmission(admission).AttestReceipt(founder, "founder",
                admission.PartyId == "planted" ? IssuedAt.AddSeconds(receivedAhead) : IssuedAt))));
        var authority = new WindowedAuthority(IssuedAt.AddSeconds(receivedAhead), permissions);
        var clock = new CountingClock();
        var admittedAt = AdmittedInstant.Read(clock);

        var reader = new VerifiedTenantRosterReader(_factory, Verifier, authority);
        Task<MemberRoster> ReadAsync() => startup
            ? reader.ReadPartialAsync(Tenant(), founder.IssuerId, admittedAt, CancellationToken.None)
            : reader.ReadAsync(Tenant(), admittedAt, CancellationToken.None);
        if (allowed)
        {
            var rebuilt = await ReadAsync();
            Assert.True(rebuilt.Contains("planted"));
        }
        else
        {
            var refused = await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(
                ReadAsync);
            Assert.Equal(VerifiedTenantRosterRefusal.Orphan, refused.Refusal);
        }
        Assert.Contains(IssuedAt.AddSeconds(30), authority.Reads);
        Assert.All(authority.Reads, instant => Assert.True(instant <= IssuedAt.AddSeconds(30)));
        Assert.Equal(1, clock.Reads);
    }

    private sealed class WindowedAuthority(DateTimeOffset startsAt, PermissionSet permissions) : IRosterAuthority
    {
        internal List<DateTimeOffset> Reads { get; } = [];

        public PermissionSet PermissionsFor(string teamId, string partyId, DateTimeOffset at)
        {
            Reads.Add(at);
            return partyId == "admin" && at >= startsAt
                ? permissions : PermissionSet.Of(TeamRolePermissions.RecordsRead);
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        internal int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return IssuedAt;
        }
    }

    private async Task AssertRefusalAsync(VerifiedTenantRosterRefusal expected, TenantId tenant)
    {
        var exception = await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(
            () => NewReader().ReadAsync(tenant, AdmittedInstant.FromRecordedAct(IssuedAt), CancellationToken.None));
        Assert.Equal(expected, exception.Refusal);
    }

    private async Task SeedAsync(IEnumerable<NodeRosterRecord> rows)
    {
        await using var db = _factory.CreateDbContext();
        db.RosterRecords.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static TenantId Tenant() => new(Team.ToString("D"));

    private static NodeRosterRecord ToRow(MemberAdmissionRecord admission, RosterFixture fixture) =>
        NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(admission)
            .AttestReceipt(fixture.FounderSigner, fixture.FounderParty, admission.Admission.IssuedAt));

    private static NodeRosterRecord ToRow(MemberRevocationRecord revocation, RosterFixture fixture) =>
        NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(revocation)
            .AttestReceipt(fixture.FounderSigner, fixture.FounderParty, revocation.Signed.IssuedAt));

    private static RosterFixture BuildRoster()
    {
        const string founderParty = "party:founder";
        const string memberParty = "party:member";
        var founderSigner = new Ed25519Signer(KeyPair.Generate());
        var member = KeyPair.Generate();
        var genesis = MemberRoster.Genesis(
            Team,
            founderParty,
            founderSigner,
            Verifier,
            IssuedAt,
            Guid.Parse("ad030000-0000-0000-0000-000000000003"));
        var roster = genesis.Admit(
            founderParty,
            founderSigner,
            memberParty,
            member.PrincipalId,
            PermissionCompositions.Member,
            Verifier,
            IssuedAt.AddMinutes(1),
            Guid.Parse("ad030000-0000-0000-0000-000000000013"));
        return new RosterFixture(
            founderParty,
            memberParty,
            founderSigner,
            roster,
            roster.EnumerateAdmissions());
    }

    private sealed record RosterFixture(
        string FounderParty,
        string MemberParty,
        Ed25519Signer FounderSigner,
        MemberRoster Roster,
        IReadOnlyList<MemberAdmissionRecord> Admissions);

    private sealed class TestRosterContextFactory : IDbContextFactory<NodeLocalRosterDbContext>
    {
        private readonly DbContextOptions<NodeLocalRosterDbContext> _options;

        internal TestRosterContextFactory(string connectionString)
        {
            _options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
                .UseSqlite(connectionString)
                .Options;
        }

        public NodeLocalRosterDbContext CreateDbContext() => new(_options);
    }

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        internal MutableActiveTeamAccessor(RuntimeTeamId initial) => Set(initial);

        public TeamContext? Active { get; private set; }

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;

        public Task SetActiveAsync(RuntimeTeamId teamId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var previous = Active;
            Set(teamId);
            ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(previous, Active));
            return Task.CompletedTask;
        }

        private void Set(RuntimeTeamId teamId) =>
            Active = new TeamContext(teamId, "ADM-03 flip", NullServiceProvider.Instance, TimeProvider.System);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        internal static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
