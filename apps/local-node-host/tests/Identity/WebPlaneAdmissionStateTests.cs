using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// #264 — the R2 mode selector must count only grants ISSUED THROUGH THE WEB PLANE. The installation founder
/// ceremony writes a bootstrap grant into the same <c>search_grants</c> table on every install, so a selector that
/// counts any live grant reports web-plane admission enabled on every founded node and the plain wire-enrollment
/// path refuses with <c>plain_path_disabled</c> — the doctrine's open/plain arm becomes unreachable in production.
/// The bootstrap grants here come from the REAL ceremony (<see cref="BootstrapClaimRedemptionService"/>), not from a
/// hand-built row, so the test pins what the installer actually writes.
/// </summary>
public sealed class WebPlaneAdmissionStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Founder_Bootstrap_Grant_Alone_Leaves_The_Plain_Path_Open()
    {
        await using var harness = await BootstrapClaimRedemptionTests.Harness.CreateAsync();

        var redeemed = await harness.Service.RedeemAsync(
            await harness.IssueClaimAsync("founder"), harness.Target);

        // The ceremony really did write a live grant row (otherwise the assertion below is vacuous).
        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed, redeemed.Status);
        // The ceremony's own row, beside the three installer authorization-seed rows every install carries.
        var bootstrap = await LiveGrantsAsync(harness, GrantSourceKind.Bootstrap);
        Assert.Contains(bootstrap, row => row.GrantId == redeemed.Grant!.GrantId.ToString());
        Assert.All(bootstrap, row => Assert.Equal((int)GranterKind.Installer, row.GranterKind));

        Assert.False(await SelectorFor(harness).IsEnabledAsync(harness.Target.TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task One_Web_Plane_Issued_Grant_Closes_The_Plain_Path()
    {
        await using var harness = await BootstrapClaimRedemptionTests.Harness.CreateAsync();
        await AddWebPlaneGrantAsync(harness);

        Assert.True(await SelectorFor(harness).IsEnabledAsync(harness.Target.TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task A_Web_Plane_Grant_Beside_The_Bootstrap_Grant_Still_Closes_The_Plain_Path()
    {
        await using var harness = await BootstrapClaimRedemptionTests.Harness.CreateAsync();
        Assert.Equal(
            BootstrapClaimRedemptionStatus.Redeemed,
            (await harness.Service.RedeemAsync(
                await harness.IssueClaimAsync("founder"), harness.Target)).Status);
        await AddWebPlaneGrantAsync(harness);

        Assert.True(await SelectorFor(harness).IsEnabledAsync(harness.Target.TenantId, CancellationToken.None));
    }

    private static LiveWebPlaneAdmissionState SelectorFor(BootstrapClaimRedemptionTests.Harness harness) =>
        new(new BootstrapClaimRedemptionTests.SharedSearchFactory(harness.DatabasePath),
            new BootstrapClaimRedemptionTests.MutableClock(Now));

    private static async Task<List<GrantRow>> LiveGrantsAsync(
        BootstrapClaimRedemptionTests.Harness harness, GrantSourceKind source)
    {
        var now = Now.ToUnixTimeMilliseconds();
        await using var context = new BootstrapClaimRedemptionTests.SharedSearchFactory(harness.DatabasePath)
            .CreateDbContext();
        return await context.Grants.AsNoTracking()
            .Where(LiveWebMembershipGrantQuery.ForTenantAt(harness.Target.TenantId, now))
            .Where(row => row.Source == (int)source)
            .ToListAsync();
    }

    /// <summary>A grant as the web plane writes one: a PERSON granter, non-bootstrap provenance.</summary>
    private static async Task AddWebPlaneGrantAsync(BootstrapClaimRedemptionTests.Harness harness)
    {
        await using var context = new BootstrapClaimRedemptionTests.SharedSearchFactory(harness.DatabasePath)
            .CreateDbContext();
        context.Grants.Add(new GrantRow
        {
            GrantId = "web-plane-grant-1", TenantId = harness.Target.TenantId.Value, SubjectId = "principal-1",
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name, ScopeValue = "/", Residency = 0,
            ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GrantedBy = "issuer-1",
            GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GranterKind = (int)GranterKind.Person,
            Source = (int)GrantSourceKind.Manual, Approver = "issuer-1",
            LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            SourceReference = "web-plane-grant-1", RevokedAtUnixMs = null, OwnerVersion = 1,
        });
        await context.SaveChangesAsync();
    }
}
