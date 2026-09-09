using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Ticket 294 slice 2b: a key-space semantic change refuses old signed roster wire records.</summary>
public sealed class RosterWireFormatRefusalTests : IAsyncLifetime
{
    private const string Tenant = "29400000-0000-4000-8000-000000000001";
    private const string Seed = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Dek = "3333333333333333333333333333333333333333333333333333333333333333";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s294-wire-" + Guid.NewGuid().ToString("N"));
    private int _compositionCompleted;
    private sealed class CompositionCaptured : Exception;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("PlanCard", "294-s2b")]
    public async Task A_version_three_receive_attestation_is_refused_with_a_named_reason()
    {
        using var pair = KeyPair.Generate();
        var signer = new Ed25519Signer(pair);
        var nonce = Guid.Parse("29400000-0000-4000-8000-000000000002");
        var receivedAt = DateTimeOffset.UnixEpoch.AddDays(294);
        var payload = new RosterReceiveAttestationRecord(3, "principal-294", signer.IssuerId.ToBase64Url(),
            "record-294", receivedAt.ToString("O"));
        var signed = await signer.SignAsync(payload, receivedAt, nonce);
        var attestation = new RosterReceiveAttestation(3, payload.NodePartyId, payload.NodePublicKey,
            receivedAt, signed.Signature.ToBase64Url());

        var accepted = RosterReceiveAttestationSigning.Verify("record-294", nonce, attestation,
            new Ed25519Verifier(), out var refusal);

        Assert.False(accepted);
        Assert.Equal(RosterReceiveAttestationSigning.WireFormatUnsupportedRefusal, refusal);
    }

    [Fact]
    [Trait("PlanCard", "294-s2b")]
    public async Task A_version_three_roster_record_refuses_the_install_at_boot_with_a_stated_remedy()
    {
        var first = await ComposeAsync();
        await PublishBootAsync(first);
        await SetWireFormatAsync(3);
        var rowsBeforeRefusal = await ReadRowsAsync();
        var completed = _compositionCompleted;

        var error = await Record.ExceptionAsync(ComposeAsync);

        AssertRefusal(error, completed);
        Assert.Equal(rowsBeforeRefusal, await ReadRowsAsync());
    }

    [Fact]
    [Trait("PlanCard", "294-s2b")]
    public async Task The_refusal_is_idempotent_across_a_restart()
    {
        var first = await ComposeAsync();
        await PublishBootAsync(first);
        await SetWireFormatAsync(3);
        var rowsBeforeRefusal = await ReadRowsAsync();
        var completed = _compositionCompleted;

        AssertRefusal(await Record.ExceptionAsync(ComposeAsync), completed);
        AssertRefusal(await Record.ExceptionAsync(ComposeAsync), completed);
        Assert.Equal(rowsBeforeRefusal, await ReadRowsAsync());
    }

    private async Task<NodeTeamRoster> ComposeAsync()
    {
        NodeTeamRoster? roster = null;
        await Assert.ThrowsAsync<CompositionCaptured>(() => global::LocalNodeHostComposition.RunAsync(
            ["--LocalNode:RootSeedHex=" + Seed, "--LocalNode:TeamId=" + Tenant,
             "--LocalNode:StoreDekHex=" + Dek, "--LocalNode:MultiTeam:Enabled=false",
             "--LocalNode:Sync:ListenForPeers=true", "--LocalNode:Sync:BindAddress=tcp://127.0.0.1:7343",
             "--urls=http://127.0.0.1:7342"],
            sessionTokenOverride: "s294-wire", dataDirectory: _directory,
            installFootprintRootOverride: _directory,
            finalServiceRegistration: services =>
            {
                _compositionCompleted++;
                roster = (NodeTeamRoster)Assert.Single(services,
                    item => item.ServiceType == typeof(NodeTeamRoster)).ImplementationInstance!;
                throw new CompositionCaptured();
            }));
        return Assert.IsType<NodeTeamRoster>(roster);
    }

    private ServiceProvider Store(NodeTeamRoster? roster = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContextWithStoreDek(Convert.FromHexString(Dek), DatabasePath);
        services.AddSingleton<IOperationVerifier, Ed25519Verifier>();
        services.AddSingleton<IOperationSigner>(new Ed25519Signer(KeyPair.FromSeed(Convert.FromHexString(Seed))));
        services.AddSingleton(TimeProvider.System);
        if (roster is not null) services.AddSingleton(roster);
        services.AddNodeRoster();
        return services.BuildServiceProvider();
    }

    private async Task PublishBootAsync(NodeTeamRoster roster)
    {
        await using var provider = Store(roster);
        var bootstrap = new RosterSyncBootstrapHostedService(
            provider.GetRequiredService<IDeltaRouter>(), provider.GetRequiredService<RosterCrdtProjection>(),
            NullLogger<RosterSyncBootstrapHostedService>.Instance, roster);
        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StopAsync(CancellationToken.None);
    }

    private async Task SetWireFormatAsync(int version)
    {
        await using var provider = Store();
        await using var db = await provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>()
            .CreateDbContextAsync();
        (await db.RosterRecords.SingleAsync()).WireFormatVersion = version;
        await db.SaveChangesAsync();
    }

    private async Task<string> ReadRowsAsync()
    {
        await using var provider = Store();
        await using var db = await provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>()
            .CreateDbContextAsync();
        return System.Text.Json.JsonSerializer.Serialize(await db.RosterRecords.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
    }

    private void AssertRefusal(Exception? error, int completed)
    {
        var refusal = Assert.IsType<InvalidOperationException>(error?.InnerException);
        Assert.Equal(GenesisStartupMessages.RosterWireFormatPre294, refusal.Message);
        Assert.Equal(completed, _compositionCompleted);
    }

    private string DatabasePath => Path.Combine(_directory, "local-node.db");
}
