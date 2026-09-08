using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.NodeHosting;
using Harborline.Api.Protocol.Client;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using HostUnderTest = Harborline.Api.NodeHosting.NodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>D5 acceptance: the client writes and reads a persisted node contact.</summary>
[Collection("Harborline process environment")]
public sealed class NodeHostingAcceptanceE2ETests : IAsyncLifetime
{
    private const string TestFounderUsername = "harborline-d5-founder";

    private HostUnderTest? _nodeHost;
    private HarborlineClient? _client;
    private string? _dataDirectory;
    private string? _previousRootSeedHex;
    private string? _previousWebClientEnabled;
    private string? _previousFounderUsername;
    private string? _previousFounderPasswordHash;
    private string? _previousClientDataDirectory;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"node-hosting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        Environment.SetEnvironmentVariable(
            "LocalNode__RootSeedHex",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        _previousWebClientEnabled = Environment.GetEnvironmentVariable("LocalNode__WebClient__Enabled");
        _previousFounderUsername = Environment.GetEnvironmentVariable("LocalNode__WebClient__FounderUsername");
        _previousFounderPasswordHash = Environment.GetEnvironmentVariable("LocalNode__WebClient__FounderPasswordHash");
        _previousClientDataDirectory = Environment.GetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY");
        Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", "true");
        Environment.SetEnvironmentVariable("LocalNode__WebClient__FounderUsername", TestFounderUsername);
        Environment.SetEnvironmentVariable(
            "LocalNode__WebClient__FounderPasswordHash",
            new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions()))
                .HashPassword(NodeWebUser.Instance, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY", _dataDirectory);

        _nodeHost = new HostUnderTest();
        var node = await _nodeHost.StartAsync(CancellationToken.None);
        _client = new HarborlineClient();
        await _client.BootstrapAsync(node.NodeAddress, node.SessionToken, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_nodeHost is not null)
            await _nodeHost.StopAsync(CancellationToken.None);
        if (_dataDirectory is not null && Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
        Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", _previousRootSeedHex);
        Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", _previousWebClientEnabled);
        Environment.SetEnvironmentVariable("LocalNode__WebClient__FounderUsername", _previousFounderUsername);
        Environment.SetEnvironmentVariable("LocalNode__WebClient__FounderPasswordHash", _previousFounderPasswordHash);
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY", _previousClientDataDirectory);
    }

    [Fact(DisplayName =
        "260 S-I: the node reads its data directory from the same variable name this harness sets — "
        + "renaming one side alone silently drops the test into the process-local default")]
    public void NodeHost_reads_the_data_directory_variable_this_harness_sets()
        => Assert.Equal(_dataDirectory, Environment.GetEnvironmentVariable("LocalNode__DataDirectory"));

    [Fact(DisplayName = "D5: POST contact then GET contact proves persistence and audit envelope")]
    public async Task ContactRoundTrip_PersistsRow_AndLeavesAuditEnvelope()
    {
        var services = LocalNodeHostRuntime.CurrentServices;
        Assert.NotNull(services);
        var peopleFactory = services!.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        long auditSequenceBefore;
        await using (var identityBefore = await services
            .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>()
            .CreateDbContextAsync())
        {
            auditSequenceBefore = await identityBefore.AuditEnvelopes
                .AsNoTracking()
                .Select(envelope => (long?)envelope.Sequence)
                .MaxAsync() ?? 0;
        }

        var created = await _client!.CreateContactAsync("W2 acceptance contact");
        var read = await _client.GetContactAsync(created.ContactId);

        Assert.Equal(created.ContactId, read.ContactId);
        Assert.Equal("W2 acceptance contact", read.DisplayName);

        await using (var people = await peopleFactory.CreateDbContextAsync())
        {
            var row = await people.Set<Party>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == new PartyId(created.ContactId));
            Assert.NotNull(row);
            Assert.Equal("W2 acceptance contact", row!.DisplayName);
        }

        var identityFactory = services.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
        await using var identity = await identityFactory.CreateDbContextAsync();
        var audit = await identity.AuditEnvelopes
            .AsNoTracking()
            .Where(envelope => envelope.Sequence > auditSequenceBefore)
            .OrderByDescending(envelope => envelope.Sequence)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.Contains(created.ContactId, audit!.CorrelationId, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(audit.EnvelopeHash));
        Assert.False(string.IsNullOrWhiteSpace(audit.PayloadDigest));
    }
}
