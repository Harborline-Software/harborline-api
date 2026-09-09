using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 293 slice 3c. The replicated path's authority is the LOCAL GRANT STORE, bound in the host's own
/// composition (<c>AddNodeRoster</c>). Every case here resolves <see cref="IRosterAuthority"/> from the composed
/// provider - never a test double - so a missing binding is a red test, not a silently narrower node.
/// </summary>
public sealed class RosterAuthorityBindingTests
{
    private static readonly Guid Team = Guid.Parse("29300000-0000-0000-0000-0000000003c0");
    private static readonly TenantId Tenant = new(Team.ToString("D"));
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);

    private static readonly PermissionSet Admitter =
        PermissionSet.Of(Permission.MembersAdmit, Permission.MembersRevoke, TeamRolePermissions.RecordsRead);
    private static readonly PermissionSet PlainMember = PermissionSet.Of(TeamRolePermissions.RecordsRead);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_synced_admission_is_accepted_only_when_the_admitter_holds_members_admit(bool admitterGranted)
    {
        await using var store = await SearchTestStore.CreateAsync();
        var signers = await SeedChainAsync(store, admitterGranted ? Admitter : PlainMember);
        await using var host = ComposedHost(store, signers.Founder);

        var reader = host.GetRequiredService<IVerifiedTenantRosterReader>();

        // "chain" was admitted by "admin", whose authority comes from the grant store alone. Without the
        // grant the record is not reachable from genesis at all, and the durable read refuses fail-closed.
        if (!admitterGranted)
        {
            var refusal = await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(
                () => reader.ReadAsync(Tenant, default));
            Assert.Contains("not reachable", refusal.Message, StringComparison.Ordinal);
            return;
        }

        var rebuilt = await reader.ReadAsync(Tenant, default);
        Assert.Contains(rebuilt.Members, m => m.PartyId == "admin");
        Assert.Contains(rebuilt.Members, m => m.PartyId == "chain");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_synced_revocation_is_applied_only_when_the_revoker_holds_members_revoke(bool revokerGranted)
    {
        await using var store = await SearchTestStore.CreateAsync();
        // The admitter always holds members:admit here, so "chain" is always admitted; only members:revoke varies.
        var signers = await SeedChainAsync(
            store,
            revokerGranted ? Admitter : PermissionSet.Of(Permission.MembersAdmit, TeamRolePermissions.RecordsRead),
            revokeChain: true);
        await using var host = ComposedHost(store, signers.Founder);

        var rebuilt = await host.GetRequiredService<IVerifiedTenantRosterReader>().ReadAsync(Tenant, default);

        // The admission stands either way; the signed revocation is applied only when the revoker's grant
        // carries members:revoke, and is dropped otherwise.
        Assert.Contains(rebuilt.EnumerateAdmissions(), a => a.PartyId == "chain");
        Assert.Equal(!revokerGranted, rebuilt.Members.Any(m => m.PartyId == "chain"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_enrolling_joiner_adopts_a_chained_admission_only_with_the_admitters_grant(bool admitterGranted)
    {
        await using var store = await SearchTestStore.CreateAsync();
        var signers = await SeedChainAsync(store, admitterGranted ? Admitter : PlainMember);
        await using var host = ComposedHost(store, signers.Founder);
        var authority = host.GetRequiredService<IRosterAuthority>();

        var founderTransport = KeyPair.Generate().PrincipalId.AsSpan().ToArray();
        var transport = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["founder"] = founderTransport,
        };
        var response = WireEnrollment.BuildResponse(signers.Local, founderTransport, transport);

        // B is "chain": it joined through "admin", not through the genesis root, so its adoption stands or
        // falls on the admitter's grant - read here through the SAME composed binding the projection uses.
        var plan = WireEnrollment.ValidateAndPlanAdoption(
            response, response.Anchor, signers.ChainKey, "chain", new Ed25519Verifier(), authority);

        Assert.Equal(admitterGranted, plan.Succeeded);
        if (!admitterGranted) Assert.Equal("joiner_not_in_roster", plan.FailureReason);
    }

    // ---- fixture ------------------------------------------------------------------------------------------

    private sealed record Signers(Ed25519Signer Founder, Ed25519Signer Admin, PrincipalId ChainKey, MemberRoster Local);

    /// <summary>
    /// founder (genesis) admits "admin" with <paramref name="adminPermissions"/>; "admin" admits "chain".
    /// The durable rows carry the pre-version-3 signed atoms, and the boot backfill turns them into the grants
    /// the replicated path now reads. Optionally appends admin's signed revocation of "chain".
    /// </summary>
    private static async Task<Signers> SeedChainAsync(
        SearchTestStore store, PermissionSet adminPermissions, bool revokeChain = false)
    {
        var verifier = new Ed25519Verifier();
        var founder = new Ed25519Signer(KeyPair.Generate());
        var admin = new Ed25519Signer(KeyPair.Generate());
        var chainKey = KeyPair.Generate().PrincipalId;

        var roster = MemberRoster.Genesis(Team, "founder", founder, verifier, At, Guid.NewGuid());
        roster = roster.Admit("founder", founder, "admin", admin.IssuerId, Admitter, verifier, At, Guid.NewGuid());
        roster = roster.Admit("admin", admin, "chain", chainKey, PlainMember, verifier, At, Guid.NewGuid());

        MemberRevocationRecord? revocation = null;
        if (revokeChain)
            revocation = roster.SignRevoke("admin", admin, "chain", verifier, At.AddSeconds(1), Guid.NewGuid()).Signed;

        NodeRosterRecord Row(MemberAdmissionRecord record, PermissionSet granted)
        {
            var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(record)
                .AttestReceipt(founder, "founder", record.Admission.IssuedAt));
            // What this party is GRANTED - the only thing the one-time backfill can convert.
            row.SignedPermissionsJson = System.Text.Json.JsonSerializer.Serialize(granted.Permissions.ToArray());
            return row;
        }

        var admissions = roster.EnumerateAdmissions().ToDictionary(a => a.PartyId, StringComparer.Ordinal);

        // The genesis root and the party IT admitted are reachable without any grant (the self-admission is the
        // root evidence), so the frozen slice 3a backfill can convert exactly those two into grants first.
        await using (var db = store.CreateRosterContext())
        {
            await db.Database.MigrateAsync();
            db.RosterRecords.Add(Row(admissions["founder"], roster.PermissionsOf("founder") ?? PermissionSet.Empty));
            db.RosterRecords.Add(Row(admissions["admin"], adminPermissions));
            await db.SaveChangesAsync();
        }

        var converted = await new RosterAdmissionGrantBackfill(
            new RosterFactory(store),
            new NodeEfAuthorizationConfigurationStore(store.Factory, new InMemoryRoleVocabulary()),
            verifier,
            NullLogger<RosterAdmissionGrantBackfill>.Instance).RunAsync();
        Assert.Equal(2, converted);

        // The CHAINED admission (and its revocation) arrive over sync afterwards - the records whose fate the
        // grant store now decides.
        await using (var db = store.CreateRosterContext())
        {
            db.RosterRecords.Add(Row(admissions["chain"], PlainMember));
            if (revocation is not null)
                db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(
                    RosterRecordCrdtState.FromRevocation(revocation).AttestReceipt(
                        admin, "admin", revocation.Signed.IssuedAt)));
            await db.SaveChangesAsync();
        }

        return new Signers(founder, admin, chainKey, roster);
    }

    private static ServiceProvider ComposedHost(SearchTestStore store, Ed25519Signer signer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationSigner>(signer);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(store.Factory);
        services.AddSingleton<IDbContextFactory<NodeLocalRosterDbContext>>(new RosterFactory(store));
        services.AddNodeRoster();
        services.AddNodeAuthorizationModel();
        return services.BuildServiceProvider();
    }

    private sealed class RosterFactory(SearchTestStore store) : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => store.CreateRosterContext();
    }
}
