using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Plane-aware (Option A, admiral-ruling-2026-07-23T0045Z) membership admission. A web membership is
/// grant-anchored: it is admitted by the live Party binding plus a valid live grant + authorization
/// epoch, WITHOUT requiring signed atlas-roster presence (the joiner's atlas admission is deferred to
/// the #3107 bridge). The grant / owner-version / epoch teeth stay mandatory for every member, so a
/// drifted, revoked, or missing grant is still refused — grant revocation is the web-plane revocation
/// lever.
/// </summary>
public sealed class LiveTenantMembershipAuthorityAdmissionTests
{
    private const string TenantId = "7e57aaaa-0000-0000-0000-000000000001";
    private const string PrincipalId = "principal-1";
    private const string PartyId = "party-1";
    private const string GrantId = "grant-1";
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Grant_Anchored_Membership_Is_Admitted_Without_Roster_Presence()
    {
        // A valid live grant + epoch admits the party binding even though the joiner is NOT yet on the
        // signed atlas roster (Option A grant-anchored web membership; the deferred #3107 case).
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var admission = CreateAdmission(store);

        await AssertAdmittedAsync(admission);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Missing_Party_Binding_Refuses()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var admission = new LiveTenantMembershipAuthorityAdmission(
            new NullPartyReader(),
            store.Factory,
            new FixedTimeProvider(Now));

        await AssertRefusedAsync(admission);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Revoked_Grant_Refuses_A_Grant_Anchored_Membership()
    {
        // Grant revocation is the sufficient revocation lever for a grant-anchored web member.
        await using var store = await SeedGrantAuthorityAsync(
            ownerVersion: 4, authorizationEpoch: 7, revokedAtUnixMs: Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var admission = CreateAdmission(store);

        await AssertRefusedAsync(admission);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Grant_Owner_Version_Drift_Refuses()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 5, authorizationEpoch: 7);
        var admission = CreateAdmission(store);

        await AssertRefusedAsync(admission);
    }

    /// <summary>
    /// Ticket 362 slice 3 — an ADVANCED live epoch admits and is handed back as the re-pin value. An
    /// administrator's narrowing or revocation advances the member's per-principal epoch and nothing
    /// refreshes the membership document, so the pin is a monotone floor: the caller re-pins the
    /// snapshot it mints a session from to the value returned here. This widens nothing — the grant row
    /// and owner version are still matched exactly, the closure is re-derived from the live grants, and
    /// an already-minted session pinning the OLD epoch is still refused by the session authority.
    /// </summary>
    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Advanced_Authorization_Epoch_Is_Admitted_And_Returned_As_The_Re_Pin()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 8);
        var admission = CreateAdmission(store);

        Assert.Equal(8, await admission.ValidateExistingAsync(
            "account-1", Membership(), CancellationToken.None));
    }

    /// <summary>A live epoch BELOW the pin is a rollback: the fence only ever moves forward.</summary>
    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Authorization_Epoch_Below_The_Pin_Refuses()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 6);
        var admission = CreateAdmission(store);

        await AssertRefusedAsync(admission);
    }

    private static LiveTenantMembershipAuthorityAdmission CreateAdmission(SearchTestStore store) =>
        new(new FixedPartyReader(), store.Factory, new FixedTimeProvider(Now));

    private static async Task AssertAdmittedAsync(LiveTenantMembershipAuthorityAdmission admission) =>
        Assert.Equal(
            7,
            await admission.ValidateExistingAsync("account-1", Membership(), CancellationToken.None));

    private static async Task AssertRefusedAsync(LiveTenantMembershipAuthorityAdmission admission)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admission.ValidateExistingAsync("account-1", Membership(), CancellationToken.None));

        Assert.Contains("identity.membership_unavailable", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<SearchTestStore> SeedGrantAuthorityAsync(
        long ownerVersion,
        long authorizationEpoch,
        long? revokedAtUnixMs = null)
    {
        var store = await SearchTestStore.CreateAsync();
        await using var context = store.CreateContext();
        context.Grants.Add(new GrantRow
        {
            GrantId = GrantId,
            TenantId = TenantId,
            SubjectId = PrincipalId,
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
            ScopeValue = "/",
            Residency = 0,
            ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            GrantedBy = "issuer-1",
            GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            Status = revokedAtUnixMs is null ? (int)GrantStatus.Active : (int)GrantStatus.Revoked,
            GranterKind = (int)GranterKind.Person,
            Source = (int)GrantSourceKind.Manual,
            Approver = "issuer-1",
            LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            RevokedBy = revokedAtUnixMs is null ? null : "issuer-1",
            RevokedAtUnixMs = revokedAtUnixMs,
            RevocationReasonCode = revokedAtUnixMs is null ? null : GrantReasonCodes.RevocationOffboarding,
            OwnerVersion = ownerVersion,
        });
        context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
        {
            TenantId = TenantId,
            PrincipalId = PrincipalId,
            AuthorizationEpoch = authorizationEpoch,
        });
        await context.SaveChangesAsync();
        return store;
    }

    private static TenantMembershipSnapshot Membership() =>
        new(
            "membership-1",
            "account-1",
            TenantId,
            PrincipalId,
            GrantId,
            GrantOwnerVersion: 4,
            AuthorizationEpoch: 7,
            TenantMembershipStatus.Active,
            OwnerVersion: 3);

    private sealed class FixedPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(PartyId)));
    }

    private sealed class NullPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
