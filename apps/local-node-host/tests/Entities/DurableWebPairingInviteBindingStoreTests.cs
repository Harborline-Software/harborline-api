using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.LocalNodeHost.Data.Admission;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 #3167 (R6/D) — durability coverage for the SQLCipher-backed <see cref="DurableWebPairingInviteBindingStore"/>.
/// Proves a device-pairing binding SURVIVES a node restart (a fresh store over the same file returns it) with every
/// web-plane pin + the mint-session evidence round-tripping intact, and that an unknown token fails closed (null).
/// Single-use is NOT this store's job (that is the token store's atomic CAS Redeem); this is a pure keyed lookup.
/// </summary>
public sealed class DurableWebPairingInviteBindingStoreTests : IAsyncLifetime
{
    private const string TenantId = "7e57aaaa-0000-0000-0000-000000000001";
    private string _dir = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"harborline-pairing-binding-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _connectionString = $"Data Source={Path.Combine(_dir, "admission.db")};Pooling=False";

        await using var bootSp = BuildProvider();
        var factory = bootSp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalAdmissionDbContext>(opt => opt.UseSqlite(_connectionString));
        return services.BuildServiceProvider();
    }

    private (ServiceProvider Sp, DurableWebPairingInviteBindingStore Store) NewStore()
    {
        var sp = BuildProvider();
        var store = new DurableWebPairingInviteBindingStore(
            sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>());
        return (sp, store);
    }

    private static TenantMembershipSnapshot SampleMembership() => new(
        MembershipId: "membership-1",
        AccountId: "account-1",
        TenantId: TenantId,
        CanonicalPrincipalId: "principal-1",
        GrantId: "grant-1",
        GrantOwnerVersion: 4,
        AuthorizationEpoch: 7,
        Status: TenantMembershipStatus.Active,
        OwnerVersion: 3);

    private static TeamTrustAnchor SampleAnchor() => new(
        TenantId,
        "founder",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    [Fact]
    public async Task Binding_survives_a_restart_with_all_pins_and_session_evidence_intact()
    {
        var membership = SampleMembership();
        var anchor = SampleAnchor();
        var binding = new WebPairingInviteBinding(
            TokenId: "pairing-token-1",
            Membership: membership,
            BoundPartyId: "party-1",
            Anchor: anchor,
            SessionCorrelationId: "session-corr-1");

        // ── BOOT 1: bind, then "shut down". ──
        var (sp1, store1) = NewStore();
        try
        {
            var factory = sp1.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
            new DurableAdmissionTokenStore(factory).Issue(new AdmissionToken(
                binding.TokenId, anchor, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)));
            store1.Bind(binding);
        }
        finally { await sp1.DisposeAsync(); }

        // ── BOOT 2: a brand-new store over the SAME file — the binding must round-trip verbatim. ──
        var (sp2, store2) = NewStore();
        try
        {
            var back = store2.Lookup("pairing-token-1");
            Assert.NotNull(back);
            Assert.Equal("pairing-token-1", back!.TokenId);
            Assert.Equal("party-1", back.BoundPartyId);
            Assert.Equal("session-corr-1", back.SessionCorrelationId);
            Assert.Equal(anchor, back.Anchor);
            // Every web-plane pin round-trips (the durable snapshot carries the coordinates the redemption re-reads).
            Assert.Equal(membership.MembershipId, back.Membership.MembershipId);
            Assert.Equal(membership.AccountId, back.Membership.AccountId);
            Assert.Equal(membership.TenantId, back.Membership.TenantId);
            Assert.Equal(membership.CanonicalPrincipalId, back.Membership.CanonicalPrincipalId);
            Assert.Equal(membership.GrantId, back.Membership.GrantId);
            Assert.Equal(membership.GrantOwnerVersion, back.Membership.GrantOwnerVersion);
            Assert.Equal(membership.AuthorizationEpoch, back.Membership.AuthorizationEpoch);
            Assert.Equal(membership.Status, back.Membership.Status);
            Assert.Equal(membership.OwnerVersion, back.Membership.OwnerVersion);
        }
        finally { await sp2.DisposeAsync(); }
    }

    [Fact]
    public async Task Unknown_token_lookup_is_null_fail_closed()
    {
        var (sp, store) = NewStore();
        try
        {
            Assert.Null(store.Lookup("never-bound"));
            Assert.Null(store.Lookup(""));
            Assert.Null(store.Lookup("   "));
        }
        finally { await sp.DisposeAsync(); }
    }

    [Fact]
    public async Task Rebinding_a_token_overwrites_the_prior_row()
    {
        var (sp, store) = NewStore();
        try
        {
            var anchor = SampleAnchor();
            var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
            new DurableAdmissionTokenStore(factory).Issue(new AdmissionToken(
                "t", anchor, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)));
            store.Bind(new WebPairingInviteBinding("t", SampleMembership(), "party-1", anchor, "sess-1"));
            store.Bind(new WebPairingInviteBinding("t", SampleMembership(), "party-2", anchor, "sess-2"));
            var back = store.Lookup("t");
            Assert.NotNull(back);
            Assert.Equal("party-2", back!.BoundPartyId);
            Assert.Equal("sess-2", back.SessionCorrelationId);
        }
        finally { await sp.DisposeAsync(); }
    }
}
