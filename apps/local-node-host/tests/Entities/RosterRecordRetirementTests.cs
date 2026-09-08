using System.Text.Json;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterRecordRetirementTests
{
    [Fact]
    public async Task Production_hydration_counts_legacy_rows_once_at_information()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var localSigner = new Ed25519Signer(KeyPair.Generate());
        await using (var db = store.CreateRosterContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260908030000_RosterAddRehostGrantBurns");
            foreach (var record in Fixture(localSigner).EnumerateAdmissions())
            {
                var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(record));
                row.PermissionsJson = "[\"planted:permission\"]";
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO roster_records
                        (id, kind, team_id, party_id, public_key, permissions, admitted_by_key,
                         admitted_by_party, nonce, signature, is_genesis, issued_at)
                    VALUES ({row.Id}, {row.Kind}, {row.TeamId}, {row.PartyId}, {row.PublicKeyB64Url},
                        {row.PermissionsJson}, {row.AdmittedByPublicKey}, {row.AdmittedByPartyId},
                        {row.NonceGuid}, {row.SignatureB64Url}, {row.IsGenesis}, {row.IssuedAtUtc.ToUnixTimeMilliseconds()})
                    """);
            }
            await db.Database.MigrateAsync();
            foreach (var row in await db.RosterRecords.ToListAsync())
            {
                var state = NodeRosterRecord.ToCrdtState(row)
                    .AttestReceipt(localSigner, "founder", row.IssuedAtUtc);
                row.ReceivedAtUtc = DateTimeOffset.Parse(state.ReceivedAtIso);
                row.WireFormatVersion = state.WireFormatVersion;
                row.ReceivedByPartyId = state.ReceivedByPartyId;
                row.ReceivedByPublicKey = state.ReceivedByPublicKey;
                row.ReceiveAttestationSignatureB64Url = state.ReceiveAttestationSignatureB64Url;
            }
            await db.SaveChangesAsync();
        }
        var logger = new LegacyLogger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationSigner>(localSigner);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<NodeLocalRosterDbContext>>(new RosterFactory(store));
        services.AddNodeRoster();
        services.AddSingleton<ILogger<RosterCrdtProjection>>(logger);
        await using var provider = services.BuildServiceProvider();
        var projection = provider.GetRequiredService<RosterCrdtProjection>();
        await projection.HydrateFromStoreAsync(default);
        await projection.HydrateFromStoreAsync(default);
        Assert.Equal(new[] { 2 }, logger.Counts);
        Assert.All(projection.Snapshot(), state => Assert.Empty(state.Permissions));
        await using var reopened = store.CreateRosterContext();
        Assert.False(reopened.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Member_record_contains_membership_evidence_only_and_grants_keep_snapshot_semantics()
    {
        Assert.Equal(new[] { "Admission", "PartyId", "PublicKey" },
            typeof(RosterMember).GetProperties().Select(p => p.Name).Order().ToArray());
        var roster = Fixture();
        var narrowed = roster.Grant("founder", "member", PermissionSet.Empty);
        Assert.Equal(PermissionCompositions.Member, roster.PermissionsOf("member"));
        Assert.Equal(PermissionSet.Empty, narrowed.PermissionsOf("member"));
        Assert.Equal(roster.Find("member"), narrowed.Find("member"));
        Assert.True(narrowed.ValidatesToGenesis(new Ed25519Verifier()));
    }

    [Fact]
    public void Durable_read_ignores_the_legacy_permission_field_without_parsing_it()
    {
        var record = Fixture().EnumerateAdmissions().Single(a => a.PartyId == "member");
        var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(record));
        row.PermissionsJson = "this legacy value is deliberately not JSON";
        var restored = NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull()!;
        Assert.True(RosterSigning.VerifyAdmission(Guid.Parse(record.TeamId), record.PartyId,
            record.PublicKey, restored.Admission, new Ed25519Verifier()));
        Assert.Equal(record.Permissions, restored.Permissions);
    }

    [Fact]
    public void New_durable_evidence_round_trips_and_missing_evidence_cannot_reuse_legacy_atoms()
    {
        var record = Fixture().EnumerateAdmissions().Single(a => a.PartyId == "member");
        var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(record));
        var evidence = typeof(NodeRosterRecord).GetProperty("SignedPermissionsJson");
        Assert.NotNull(evidence);
        var signed = JsonSerializer.Deserialize<string[]>((string)evidence.GetValue(row)!)!;
        Assert.Equal(record.Permissions, PermissionSet.From(signed));
        row.PermissionsJson = JsonSerializer.Serialize(record.Permissions.Permissions);
        evidence.SetValue(row, "");
        var restored = NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull()!;
        Assert.Equal(PermissionSet.Empty, restored.Permissions);
        Assert.False(RosterSigning.VerifyAdmission(Guid.Parse(record.TeamId), record.PartyId,
            record.PublicKey, restored.Admission, new Ed25519Verifier()));
    }

    private static MemberRoster Fixture(IOperationSigner? localSigner = null)
    {
        var founder = localSigner ?? new Ed25519Signer(KeyPair.Generate());
        var verifier = new Ed25519Verifier();
        return MemberRoster.Genesis(Guid.NewGuid(), "founder", founder, verifier,
                DateTimeOffset.UnixEpoch, Guid.NewGuid())
            .Admit("founder", founder, "member", KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
    }

    private sealed class RosterFactory(SearchTestStore store) : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => store.CreateRosterContext();
    }

    private sealed class LegacyLogger : ILogger<RosterCrdtProjection>
    {
        public List<int> Counts { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Ignored legacy roster permission fields", StringComparison.Ordinal)) return;
            Assert.Equal(LogLevel.Information, level);
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(state);
            Counts.Add(Assert.IsType<int>(fields.Single(p => p.Key == "Count").Value));
        }
    }
}
