using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class DurableGenesisBootTests : IAsyncLifetime
{
    private const string Tenant = "aaaaaaaa-0000-0000-0000-000000000296";
    private const string OtherTenant = "bbbbbbbb-0000-0000-0000-000000000296";
    private const string Seed = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Dek = "3333333333333333333333333333333333333333333333333333333333333333";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s296-" + Guid.NewGuid().ToString("N"));
    private int _compositionCompleted;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()
    {
        // Clear only this fixture's connection pool, never the process-wide pool.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
    private string DatabasePath => Path.Combine(_directory, "local-node.db");
    private sealed class CompositionCaptured : Exception;

    private async Task<NodeTeamRoster> ComposeAsync(Func<string> account, bool recoverable = true, string seed = Seed,
        string tenant = Tenant)
    {
        NodeTeamRoster? roster = null;
        await Assert.ThrowsAsync<CompositionCaptured>(() => global::LocalNodeHostComposition.RunAsync(
            ["--LocalNode:RootSeedHex=" + seed, "--LocalNode:TeamId=" + tenant,
             "--LocalNode:StoreDekHex=" + (recoverable ? Dek : ""),
             "--LocalNode:MultiTeam:Enabled=false", "--LocalNode:Sync:ListenForPeers=true",
             "--LocalNode:Sync:BindAddress=tcp://127.0.0.1:7303", "--urls=http://127.0.0.1:7302"],
            sessionTokenOverride: "s296-composition", dataDirectory: _directory,
            installFootprintRootOverride: _directory, genesisAccountName: account,
            finalServiceRegistration: services =>
            {
                _compositionCompleted++;
                roster = (NodeTeamRoster)Assert.Single(services,
                    item => item.ServiceType == typeof(NodeTeamRoster)).ImplementationInstance!;
                throw new CompositionCaptured();
            }));
        return Assert.IsType<NodeTeamRoster>(roster);
    }

    private ServiceProvider Store(bool recoverable = true, NodeTeamRoster? roster = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (recoverable)
            services.AddSqlCipherLocalNodeDbContextWithStoreDek(Convert.FromHexString(Dek), DatabasePath);
        else
            services.AddSqlCipherLocalNodeDbContext(Convert.FromHexString(Seed), DatabasePath, new SqlCipherKeyDerivation());
        services.AddSingleton<IOperationVerifier, Ed25519Verifier>();
        if (roster is not null) services.AddSingleton(roster);
        services.AddSingleton(TimeProvider.System);
        services.AddNodeRoster();
        return services.BuildServiceProvider();
    }

    private async Task PublishBootAsync(NodeTeamRoster roster, bool recoverable = true)
    {
        await using var provider = Store(recoverable, roster);
        var bootstrap = new RosterSyncBootstrapHostedService(
            provider.GetRequiredService<IDeltaRouter>(), provider.GetRequiredService<RosterCrdtProjection>(),
            NullLogger<RosterSyncBootstrapHostedService>.Instance, roster);
        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StopAsync(CancellationToken.None);
    }

    private async Task<NodeRosterRecord[]> RowsAsync(bool recoverable = true)
    {
        await using var provider = Store(recoverable);
        await using var db = await provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>().CreateDbContextAsync();
        return await db.RosterRecords.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstBootMintsShellIdentityOnceAndPublishesOneSignedGenesis(bool recoverable)
    {
        var reads = 0;
        var roster = await ComposeAsync(() => { reads++; return "  Jose\u0301  "; }, recoverable);
        using var signer = new NodePrincipalSigner(Convert.FromHexString(Seed));
        var suffix = Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        Assert.Equal("os:Jos\u00e9#" + suffix, roster.Current.GenesisPartyId);
        Assert.Equal(1, reads);
        Assert.Empty(await RowsAsync(recoverable));
        await PublishBootAsync(roster, recoverable);
        var row = Assert.Single(await RowsAsync(recoverable));
        Assert.True(row.IsGenesis);
        Assert.Equal(roster.Current.GenesisPartyId, row.PartyId);
        Assert.True(roster.Current.ValidatesToGenesis(new Ed25519Verifier()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountRenameReadsBackOriginalSignedRecordWithoutConsultingShell(bool recoverable)
    {
        var first = await ComposeAsync(() => "original-account", recoverable);
        await PublishBootAsync(first, recoverable);
        var before = JsonSerializer.Serialize(await RowsAsync(recoverable));
        var renamedAccountReads = 0;
        var second = await ComposeAsync(() => { renamedAccountReads++; return "renamed-service-account"; }, recoverable);
        Assert.Equal(0, renamedAccountReads);
        Assert.Equal(first.Current.GenesisPartyId, second.Current.GenesisPartyId);
        Assert.Equal(JsonSerializer.Serialize(first.Current.EnumerateAdmissions().Single().Admission),
            JsonSerializer.Serialize(second.Current.EnumerateAdmissions().Single().Admission));
        await PublishBootAsync(second, recoverable);
        Assert.Single(await RowsAsync(recoverable));
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync(recoverable)));
    }

    [Fact]
    public async Task EnrolledNodeWithoutOwnTenantLogMintsOnBoot()
    {
        var first = await ComposeAsync(() => "original-account");
        await PublishBootAsync(first);
        await using (var provider = Store(roster: first))
            await provider.GetRequiredService<RosterCrdtProjection>()
                .SupersedeOwnTeamRecordsAsync(Guid.Parse(Tenant), CancellationToken.None);
        await PersistOtherTenantAsync(first.Current.GenesisPartyId);
        var foreignRows = await RowsAsync();
        var foreignBefore = JsonSerializer.Serialize(foreignRows);
        Assert.All(foreignRows, row => Assert.Equal(OtherTenant, row.TeamId));

        var reads = 0;
        var reboot = await ComposeAsync(() => { reads++; return "original-account"; });
        Assert.Equal(1, reads);
        Assert.Equal(Guid.Parse(Tenant), reboot.Current.TeamId);
        Assert.Equal(first.Current.GenesisPartyId, reboot.Current.GenesisPartyId);
        Assert.Equal(foreignBefore, JsonSerializer.Serialize(await RowsAsync()));
        await PublishBootAsync(reboot);
        var rows = await RowsAsync();
        // Existing bootstrap reconciliation retracts the local seed and adopts the joined tenant.
        Assert.All(rows, row => Assert.Equal(OtherTenant, row.TeamId));
        Assert.Equal(Guid.Parse(OtherTenant), reboot.Current.TeamId);
        Assert.True(reboot.Current.Contains(first.Current.GenesisPartyId));
        Assert.True(reboot.Current.ValidatesToGenesis(new Ed25519Verifier()));
        Assert.Equal(foreignRows.Select(row => (row.Id, row.SignatureB64Url)),
            rows.Select(row => (row.Id, row.SignatureB64Url)));
    }

    [Fact]
    public async Task OwnGenesisWithOtherTenantRecordsReadsBackOnlyOwnLog()
    {
        var first = await ComposeAsync(() => "original-account");
        await PublishBootAsync(first);
        await PersistOtherTenantAsync(first.Current.GenesisPartyId);
        var before = JsonSerializer.Serialize(await RowsAsync());
        var reads = 0;
        var reboot = await ComposeAsync(() => { reads++; return "renamed-account"; });
        Assert.Equal(0, reads);
        Assert.Equal(Guid.Parse(Tenant), reboot.Current.TeamId);
        Assert.Equal(first.Current.GenesisPartyId, reboot.Current.GenesisPartyId);
        Assert.Equal(JsonSerializer.Serialize(first.Current.EnumerateAdmissions().Single().Admission),
            JsonSerializer.Serialize(reboot.Current.EnumerateAdmissions().Single().Admission));
        await PublishBootAsync(reboot);
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    [Fact]
    public async Task EnrolledNodeConfiguredForJoinedTenantReadsItsAdmittedIdentity()
    {
        var first = await ComposeAsync(() => "original-account");
        var admittedParty = first.Current.GenesisPartyId;
        await PersistOtherTenantAsync(admittedParty);
        var before = JsonSerializer.Serialize(await RowsAsync());
        var reads = 0;
        var reboot = await ComposeAsync(() => { reads++; return "renamed-account"; }, tenant: OtherTenant);
        Assert.Equal(0, reads);
        Assert.Equal(Guid.Parse(OtherTenant), reboot.Current.TeamId);
        Assert.Equal("other-founder", reboot.Current.GenesisPartyId);
        using var signer = new NodePrincipalSigner(Convert.FromHexString(Seed));
        Assert.Equal(signer.Signer.IssuerId, reboot.Current.PublicKeyOf(admittedParty));
        Assert.Equal(NodeDmKeyDerivation.DeriveDmPublicKey(Convert.FromHexString(Seed), OtherTenant),
            reboot.DmPublicKeyOf(admittedParty));
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    private async Task PersistOtherTenantAsync(string admittedParty, bool revoked = false)
    {
        using var founder = new NodePrincipalSigner(Convert.FromHexString(new string('4', 64)));
        using var member = new NodePrincipalSigner(Convert.FromHexString(Seed));
        var verifier = new Ed25519Verifier();
        var foreign = MemberRoster.StableGenesis(Guid.Parse(OtherTenant), "other-founder", founder.Signer, verifier)
            .Admit("other-founder", founder.Signer, admittedParty, member.Signer.IssuerId,
                PermissionCompositions.Member, verifier, DateTimeOffset.Parse("2026-09-07T12:00:00Z"),
                Guid.Parse("cccccccc-0000-0000-0000-000000000296"),
                newDmPublicKey: PrincipalId.FromBytes(
                    NodeDmKeyDerivation.DeriveDmPublicKey(Convert.FromHexString(Seed), OtherTenant)).ToBase64Url());
        await using var provider = Store();
        await using var db = await provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>().CreateDbContextAsync();
        db.RosterRecords.AddRange(foreign.EnumerateAdmissions().Select(admission =>
            NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(admission))));
        if (revoked)
        {
            var removal = foreign.SignRevoke("other-founder", founder.Signer, admittedParty, verifier,
                DateTimeOffset.Parse("2026-09-07T13:00:00Z"),
                Guid.Parse("dddddddd-0000-0000-0000-000000000296"));
            db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(
                RosterRecordCrdtState.FromRevocation(removal.Signed)));
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RevokedNodeNamesRemovalAndReadmissionByCurrentMember()
    {
        var first = await ComposeAsync(() => "original-account");
        await PersistOtherTenantAsync(first.Current.GenesisPartyId, revoked: true);
        var before = JsonSerializer.Serialize(await RowsAsync());
        var completed = _compositionCompleted;
        var shellReads = 0;
        var error = await Record.ExceptionAsync(() => ComposeAsync(
            () => { shellReads++; return "renamed-account"; }, tenant: OtherTenant));
        AssertRefusal(error, "genesis_membership_removed",
            "This node has been removed from the tenant's roster", completed);
        Assert.Contains("Have a current member re-admit this node", error!.InnerException!.Message);
        Assert.DoesNotContain("Restore the root seed", error.InnerException.Message);
        Assert.Equal(0, shellReads);
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    [Fact]
    public async Task Pre291SignatureNamesReinitialisationAndExplainsWhyOldBackupCannotRestore()
    {
        var first = await ComposeAsync(() => "original-account");
        await PublishBootAsync(first);
        using var signer = new NodePrincipalSigner(Convert.FromHexString(Seed));
        await using var provider = Store();
        var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.RosterRecords.SingleAsync();
            // Freeze the pre-291 envelope: do not use the evolving AdmissionRecord/signing helper.
            var payload = new
            {
                TeamId = row.TeamId, AdmittedPartyId = row.PartyId,
                AdmittedPublicKey = row.PublicKeyB64Url, AdmittedByPartyId = row.AdmittedByPartyId,
                AdmittedByPublicKey = row.AdmittedByPublicKey, IsGenesis = row.IsGenesis,
                AdmittedDmPublicKey = row.DmPublicKeyB64Url, AdmittedXWingPublicKey = row.XWingPublicKeyB64Url,
                AdmittedViaTokenId = row.AdmittedViaTokenId, AdmittedUnderSessionEvidence = row.MintingSessionEvidence
            };
            var signed = await signer.Signer.SignAsync(payload, row.IssuedAtUtc, Guid.Parse(row.NonceGuid));
            Assert.True(new Ed25519Verifier().Verify(signed));
            row.SignatureB64Url = signed.Signature.ToBase64Url();
            await db.SaveChangesAsync();
        }
        var before = JsonSerializer.Serialize(await RowsAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DurableGenesisIdentity.ReadAsync(
            factory, Guid.Parse(Tenant), signer.Signer.IssuerId, new Ed25519Verifier(),
            CancellationToken.None));
        Assert.StartsWith("genesis_log_legacy_format:", error.Message);
        Assert.Contains("Re-initialise this development install", error.Message);
        Assert.Contains("stop the host, delete local-node.db and its -wal and -shm sidecars from the data directory", error.Message);
        Assert.Contains("restart to mint a new genesis or re-enrol through a current member", error.Message);
        Assert.Contains("A pre-291 backup has the same version-1 signatures and cannot repair this format break", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    [Fact]
    public async Task DerivedSigningIdentityMismatchRefusesBeforePublicationWithStableCodeAndRemedy()
    {
        var first = await ComposeAsync(() => "original-account");
        await PublishBootAsync(first);
        var before = JsonSerializer.Serialize(await RowsAsync());
        var completed = _compositionCompleted;
        var shellReads = 0;
        // The Store DEK still opens the same durable log; only the root-derived principal changes.
        var error = await Record.ExceptionAsync(() => ComposeAsync(
            () => { shellReads++; return "service-account"; }, seed: new string('2', 64)));
        AssertRefusal(error, "genesis_identity_mismatch", "Restore the root seed", completed);
        Assert.Contains("This node is not an admitted member of the tenant's roster", error!.InnerException!.Message);
        Assert.Contains("Have a current member re-admit this node", error.InnerException.Message);
        Assert.Equal(0, shellReads);
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("duplicate")]
    public async Task ExistingInvalidLogNeverFallsBackToMinting(string poison)
    {
        var first = await ComposeAsync(() => "original-account");
        await PublishBootAsync(first);
        await using (var provider = Store())
        await using (var db = await provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>().CreateDbContextAsync())
        {
            var row = await db.RosterRecords.SingleAsync();
            if (poison == "tampered") row.PartyId = "os:substituted#12345678";
            else
            {
                var duplicate = NodeRosterRecord.FromCrdtState(NodeRosterRecord.ToCrdtState(row));
                duplicate.Id += "-duplicate";
                db.RosterRecords.Add(duplicate);
            }
            await db.SaveChangesAsync();
        }
        var before = JsonSerializer.Serialize(await RowsAsync());
        var completed = _compositionCompleted;
        var shellReads = 0;
        var error = await Record.ExceptionAsync(() => ComposeAsync(() => { shellReads++; return "other"; }));
        AssertRefusal(error, DurableGenesisIdentity.InvalidLogCode, "Restore the install's verified roster backup", completed);
        Assert.Equal(0, shellReads);
        Assert.Equal(before, JsonSerializer.Serialize(await RowsAsync()));
    }

    private void AssertRefusal(Exception? error, string code, string remedy, int completed)
    {
        // ComposeAsync's assertion wrapper carries the actual production exception as its inner exception.
        var refusal = Assert.IsType<InvalidOperationException>(error?.InnerException);
        Assert.StartsWith(code + ":", refusal.Message);
        Assert.Contains(remedy, refusal.Message);
        Assert.Equal(completed, _compositionCompleted);
        // Refusal precedes final service registration, provider construction and every hosted service start.
    }
}
