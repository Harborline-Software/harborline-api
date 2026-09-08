using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class NodeRosterCompromisedDeviceRevocationPublisherTests
{
    [Fact]
    public async Task RevokeAsync_publishes_a_signed_revocation_and_drops_live_trust()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-device-revocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var verifier = new Ed25519Verifier();
            var founderSigner = new Ed25519Signer(KeyPair.Generate());
            var teamId = Guid.Parse("71560000-0000-0000-0000-000000000001");
            var roster = MemberRoster.Genesis(
                teamId, "operator-a", founderSigner, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
            roster = roster.Admit(
                "operator-a",
                founderSigner,
                "stolen-node",
                KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member,
                verifier,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                Guid.NewGuid());
            var liveRoster = new NodeTeamRoster(roster);
            await using var projection = new RosterCrdtProjection(TimeProvider.System,
                new YDotNetCrdtEngine(),
                factory,
                verifier,
                NullLogger<RosterCrdtProjection>.Instance,
                liveRoster);
            var audit = new CapturingAuthorizedAuditTrail();
            var publisher = new NodeRosterCompromisedDeviceRevocationPublisher(
                new NodeRosterMemberRevocationAuthority(
                    liveRoster, new RosterRevocationProjection(projection), founderSigner, verifier, audit,
                    new NodeAdministratorAuthority(
                        factory, TimeProvider.System, TestAuthorization.AllowGate())));
            var revokedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var decision = TestAuthorization.AllowedDecision(
                new TenantId(teamId.ToString("D")), "stolen-node", "members",
                TeamRolePermissions.MembersManage, founderSigner.IssuerId.ToBase64Url(), revokedAt);

            var evidence = await publisher.RevokeAsync(
                new CompromisedDeviceResponseRequest(
                    teamId.ToString("D"), "stolen-node", "operator-a"),
                "4ee2aa00-0000-0000-0000-000000000056",
                decision);

            Assert.False(liveRoster.Current.Contains("stolen-node"));
            Assert.Same(decision, Assert.Single(audit.Decisions));
            await using var reopened = await factory.CreateDbContextAsync();
            var row = Assert.Single(await reopened.RosterRecords
                .Where(item => item.Kind == (int)RosterRecordKind.Revocation)
                .ToListAsync());
            Assert.Equal(evidence.RecordId, row.Id);
            Assert.Equal(evidence.Signature, row.SignatureB64Url);
            Assert.NotNull(NodeRosterRecord.ToCrdtState(row).ToRevocationOrNull());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact(DisplayName = "ticket 290: after a handover the same revocation succeeds and writes the administrator removal")]
    public async Task RevokeAsync_writes_the_administrator_removal_for_the_revoked_party()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-device-revocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var verifier = new Ed25519Verifier();
            var founderSigner = new Ed25519Signer(KeyPair.Generate());
            var teamId = Guid.Parse("71560000-0000-0000-0000-000000000002");
            var team = teamId.ToString("D");
            var roster = MemberRoster.Genesis(
                teamId, "operator-a", founderSigner, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
            roster = roster.Admit(
                "operator-a", founderSigner, "stolen-node", KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member, verifier, DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid());
            var liveRoster = new NodeTeamRoster(roster);

            // BOTH parties hold administrative authority in the log, so the last-usable-administrator invariant
            // permits the removal — and so the revoked one would otherwise still fold as usable after the
            // revocation, which is exactly the boot projection's re-arming input (ticket 290).
            await SeedAdministratorAsync(factory, team, "operator-a");
            await SeedAdministratorAsync(factory, team, "stolen-node");

            await using var projection = new RosterCrdtProjection(TimeProvider.System,
                new YDotNetCrdtEngine(), factory, verifier,
                NullLogger<RosterCrdtProjection>.Instance, liveRoster);
            var administrators = new NodeAdministratorAuthority(
                factory, TimeProvider.System, TestAuthorization.AllowGate());
            Assert.Equal(2, (await administrators.UsableAsync()).Count);

            var publisher = new NodeRosterCompromisedDeviceRevocationPublisher(
                new NodeRosterMemberRevocationAuthority(
                    liveRoster, new RosterRevocationProjection(projection), founderSigner, verifier,
                    new CapturingAuthorizedAuditTrail(), administrators));
            var revokedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var decision = TestAuthorization.AllowedDecision(
                new TenantId(team), "stolen-node", "members",
                TeamRolePermissions.MembersManage, founderSigner.IssuerId.ToBase64Url(), revokedAt);

            await publisher.RevokeAsync(
                new CompromisedDeviceResponseRequest(team, "stolen-node", "operator-a"),
                "4ee2aa00-0000-0000-0000-000000000057",
                decision);

            Assert.False(liveRoster.Current.Contains("stolen-node"));
            var usable = await administrators.UsableAsync();
            Assert.Equal("operator-a", Assert.Single(usable).PartyId);

            // The removal is the revocation's OWN event, not a later projection repair.
            await using var reopened = await factory.CreateDbContextAsync();
            var removal = Assert.Single(await reopened.AdministratorAuthority
                .Where(record => record.PartyId == "stolen-node"
                    && record.Event != AdministratorAuthorityEvent.Established)
                .ToListAsync());
            Assert.Equal(AdministratorAuthorityEvent.Revoked, removal.Event);
            Assert.Equal(MemberRevocationReasons.DeviceLost, removal.Reason);
            Assert.True(AdministratorAuthorityRecord.VerifyChain(
                await reopened.AdministratorAuthority.OrderBy(r => r.Sequence).ToArrayAsync()));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact(DisplayName = "ticket 290: revoking the SOLE administrator refuses whole and leaves both structures untouched")]
    public async Task RevokeAsync_refuses_when_the_party_is_the_last_usable_administrator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-device-revocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var verifier = new Ed25519Verifier();
            var founderSigner = new Ed25519Signer(KeyPair.Generate());
            var teamId = Guid.Parse("71560000-0000-0000-0000-000000000003");
            var team = teamId.ToString("D");
            var roster = MemberRoster.Genesis(
                teamId, "operator-a", founderSigner, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
            roster = roster.Admit(
                "operator-a", founderSigner, "stolen-node", KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member, verifier, DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid());
            var liveRoster = new NodeTeamRoster(roster);

            // EXACTLY ONE Established row - the state production actually has, because the boot service
            // establishes only the genesis party. Revoking that party on the roster must refuse WHOLE.
            await SeedAdministratorAsync(factory, team, "operator-a");

            await using var projection = new RosterCrdtProjection(TimeProvider.System,
                new YDotNetCrdtEngine(), factory, verifier,
                NullLogger<RosterCrdtProjection>.Instance, liveRoster);
            var administrators = new NodeAdministratorAuthority(
                factory, TimeProvider.System, TestAuthorization.AllowGate());
            var auditTrail = new CapturingAuthorizedAuditTrail();

            var publisher = new NodeRosterCompromisedDeviceRevocationPublisher(
                new NodeRosterMemberRevocationAuthority(
                    liveRoster, new RosterRevocationProjection(projection), founderSigner, verifier,
                    auditTrail, administrators));
            var revokedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var decision = TestAuthorization.AllowedDecision(
                new TenantId(team), "operator-a", "members",
                TeamRolePermissions.MembersManage, founderSigner.IssuerId.ToBase64Url(), revokedAt);

            var refusal = await Assert.ThrowsAsync<LastUsableAdministratorRevocationRefusedException>(
                async () => await publisher.RevokeAsync(
                    new CompromisedDeviceResponseRequest(team, "operator-a", "operator-a"),
                    "4ee2aa00-0000-0000-0000-000000000058",
                    decision));
            Assert.Equal(NodeAdministratorAuthority.LastUsableAdministratorCode, refusal.Code);

            // BOTH structures untouched: the roster still carries the founder and published no revocation,
            // and the administrator-authority log still holds only the one establishment.
            Assert.True(liveRoster.Current.Contains("operator-a"));
            Assert.DoesNotContain(projection.Snapshot(), record => record.Kind == RosterRecordKind.Revocation);
            Assert.Equal("operator-a", Assert.Single(await administrators.UsableAsync()).PartyId);
            await using var reopened = await factory.CreateDbContextAsync();
            var only = Assert.Single(await reopened.AdministratorAuthority.ToListAsync());
            Assert.Equal(AdministratorAuthorityEvent.Established, only.Event);

            // The refusal is evidence, not silence.
            var refusals = new List<AuditRecord>();
            await foreach (var record in auditTrail.QueryAsync(
                new AuditQuery(new TenantId(team), new AuditEventType("CapabilityRevocationRefused"))))
            {
                refusals.Add(record);
            }

            Assert.Equal(decision.Request.Principal, Assert.Single(refusals).Actor);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    private static async Task SeedAdministratorAsync(
        IDbContextFactory<NodeLocalRosterDbContext> factory, string teamId, string partyId)
    {
        await using var context = await factory.CreateDbContextAsync();
        var tip = await context.AdministratorAuthority.AsNoTracking()
            .OrderByDescending(record => record.Sequence).FirstOrDefaultAsync();
        var record = new AdministratorAuthorityRecord
        {
            Sequence = (tip?.Sequence ?? 0) + 1,
            TeamId = teamId,
            PartyId = partyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = AdministratorProvenance.Recovery,
            MemberPublicKey = "cHVibGljLWtleQ",
            AdmissionSignature = "c2lnbmF0dXJl",
            AdmittedByPublicKey = "cHVibGljLWtleQ",
            AdmittedByPartyId = partyId,
            OccurredAtUtc = DateTimeOffset.UnixEpoch,
            Reason = "test-seed",
            PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash,
            Hash = string.Empty,
        };
        record.Hash = AdministratorAuthorityRecord.ComputeHash(record);
        context.AdministratorAuthority.Add(record);
        await context.SaveChangesAsync();
    }

    private sealed class CapturingAuthorizedAuditTrail : IAuthorizedAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();
        internal List<AuthorizationDecision> Decisions { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            _inner.AppendAsync(record, ct);

        public ValueTask AppendAuthorizedAsync(
            AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null)
        {
            Decisions.Add(decision);
            return _inner.AppendAuthorizedAsync(record, decision, ct, approval);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);
    }
}
