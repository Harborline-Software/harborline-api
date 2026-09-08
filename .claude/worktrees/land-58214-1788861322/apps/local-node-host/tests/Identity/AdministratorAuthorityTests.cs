using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// ADR 0066 migration step 1 — the installer principal is a STATE, not a key.
/// </summary>
/// <remarks>
/// Every test here fails against the pre-fix tree, where <c>MultiTeamBootstrapHostedService</c> re-upserted
/// the OS user as full Admin on every boot with no member public key, no admission signature, no prior-state
/// read, and no audit. Concretely: there was no durable authority record to establish once (so "second boot
/// does not re-mint" was unrepresentable), no removal path to refuse on (so the last-administrator invariant
/// had nothing to enforce), and no provenance to distinguish bootstrap from recovery.
/// </remarks>
public sealed class AdministratorAuthorityTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private string _directory = string.Empty;
    private ServiceProvider? _provider;
    private IDbContextFactory<NodeLocalRosterDbContext> _contexts = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "harborline-admin-authority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False"));
        _provider = services.BuildServiceProvider();
        _contexts = _provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using var context = await _contexts.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A held SQLite handle on Windows is not a test failure.
        }
    }

    private NodeAdministratorAuthority AuthorityAt(DateTimeOffset now) =>
        new(_contexts, new FixedClock(now), TestAuthorization.AllowGate());

    private async Task<AdministratorAuthorityResult> RecoverAtAsync(
        DateTimeOffset now,
        AdministratorCandidate candidate)
    {
        using var scope = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
            _directory, out var failure, createDirectory: false);
        Assert.NotNull(scope);
        Assert.Equal(NodeRunLockFailure.None, failure);
        return await scope!.RecoverAsync(_contexts, new FixedClock(now), candidate);
    }

    private static AdministratorCandidate Candidate(string party = "os:founder#deadbeef") =>
        new(
            TeamId: "11111111-1111-1111-1111-111111111111",
            PartyId: party,
            MemberPublicKey: "cHVibGljLWtleQ",
            AdmissionSignature: "c2lnbmF0dXJl",
            AdmittedByPublicKey: "cHVibGljLWtleQ",
            AdmittedByPartyId: party,
            IsGenesisAdmission: true);

    // ── clause 4 + 5: the installer establishes exactly once ─────────────────────────────────────────────

    [Fact]
    public async Task Installer_establishes_the_first_administrator_with_bootstrap_provenance()
    {
        var authority = AuthorityAt(T0);

        var result = await authority.EstablishByInstallerAsync(Candidate(), installerWindow: null);

        Assert.True(result.Applied);
        var administrator = Assert.Single(await authority.UsableAsync());
        Assert.Equal(AdministratorProvenance.Bootstrap, administrator.Provenance);
        // The two fields the always-on mint never supplied.
        Assert.Equal("cHVibGljLWtleQ", administrator.MemberPublicKey);
        Assert.Equal("c2lnbmF0dXJl", administrator.AdmissionSignature);
    }

    [Fact]
    public async Task An_establishing_write_with_no_key_or_signature_is_refused()
    {
        // This IS the pre-fix write: full Admin, no key, no admission signature. It is now unrepresentable
        // at the data layer rather than merely discouraged in a caller.
        var authority = AuthorityAt(T0);

        var noKey = await authority.EstablishByInstallerAsync(
            Candidate() with { MemberPublicKey = "" }, installerWindow: null);
        var noSignature = await authority.EstablishByInstallerAsync(
            Candidate() with { AdmissionSignature = "" }, installerWindow: null);

        Assert.Equal(AdministratorAuthorityOutcome.RefusedUnsignedAdmission, noKey.Outcome);
        Assert.Equal(AdministratorAuthorityOutcome.RefusedUnsignedAdmission, noSignature.Outcome);
        Assert.Empty(await authority.UsableAsync());
    }

    [Fact]
    public async Task Second_boot_does_not_re_mint()
    {
        var authority = AuthorityAt(T0);
        await authority.EstablishByInstallerAsync(Candidate(), installerWindow: null);

        // "Boot" the installer again — the same thing every restart used to do.
        var second = await AuthorityAt(T0.AddDays(1))
            .EstablishByInstallerAsync(Candidate(), installerWindow: null);
        var third = await AuthorityAt(T0.AddDays(2))
            .EstablishByInstallerAsync(Candidate(), installerWindow: null);

        Assert.Equal(AdministratorAuthorityOutcome.AlreadyEstablished, second.Outcome);
        Assert.Equal(AdministratorAuthorityOutcome.AlreadyEstablished, third.Outcome);

        // Exactly ONE authority event in the log — the establishing write, not three. (The log also carries
        // the installer-window anchor, which is bookkeeping about the window rather than authority.)
        await using var context = await _contexts.CreateDbContextAsync();
        Assert.Equal(1, await context.AdministratorAuthority
            .CountAsync(record => record.Event == AdministratorAuthorityEvent.Established));
    }

    [Fact]
    public async Task Concurrent_starts_establish_exactly_once()
    {
        // Sixteen installers racing the gate. Serializable transactions plus SQLite's single-writer lock
        // mean exactly one may read "no administrator" and write; the rest must see the winner's row.
        var candidate = Candidate();
        var starts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
                AuthorityAt(T0).EstablishByInstallerAsync(candidate, installerWindow: null)))
            .ToArray();

        var results = await Task.WhenAll(starts);

        Assert.Equal(1, results.Count(result => result.Applied));
        Assert.Equal(15, results.Count(result =>
            result.Outcome is AdministratorAuthorityOutcome.AlreadyEstablished
                or AdministratorAuthorityOutcome.InstallerSealed));

        await using var context = await _contexts.CreateDbContextAsync();
        Assert.Equal(1, await context.AdministratorAuthority
            .CountAsync(record => record.Event == AdministratorAuthorityEvent.Established));
        // And the window anchor is written exactly once even under sixteen racing writers.
        Assert.Equal(1, await context.AdministratorAuthority
            .CountAsync(record => record.Event == AdministratorAuthorityEvent.InstallerWindowOpened));
        Assert.Single(await AuthorityAt(T0).UsableAsync());
    }

    // ── clause 6: the establishing window is time-bounded ────────────────────────────────────────────────

    [Fact]
    public async Task The_installer_window_expires()
    {
        // The window is anchored on the LOG's own first observation of itself as empty, so the anchor has
        // to exist before the window can have run out.
        await AuthorityAt(T0).ObserveEmptyLogAsync();
        var authority = AuthorityAt(T0.AddDays(3));

        var result = await authority.EstablishByInstallerAsync(
            Candidate(), installerWindow: TimeSpan.FromHours(24));

        Assert.Equal(AdministratorAuthorityOutcome.WindowExpired, result.Outcome);
        Assert.Empty(await authority.UsableAsync());
        // And the recovery path is unaffected by the window — that is what stops the bound bricking a node.
        Assert.True((await RecoverAtAsync(T0.AddDays(3), Candidate())).Applied);
    }

    [Fact]
    public async Task The_installer_window_anchor_is_written_once_and_never_moves()
    {
        // The anchor used to be Directory.GetCreationTimeUtc(DataDirectory): attacker-writable, older than
        // the feature on every existing directory, and re-derived as `now + window` on EVERY boot whenever
        // reading it faulted — so the window failed open permanently rather than once. A row in the log has
        // none of those properties, and this pins the two that matter: written once, never moved.
        var opened = await AuthorityAt(T0).ObserveEmptyLogAsync();
        Assert.True(opened.Applied);

        // Twenty later boots, each one seeing an empty log. None of them re-opens the window.
        for (var day = 1; day <= 20; day++)
        {
            await AuthorityAt(T0.AddDays(day)).ObserveEmptyLogAsync();
        }

        await using var context = await _contexts.CreateDbContextAsync();
        var anchors = await context.AdministratorAuthority.AsNoTracking()
            .Where(record => record.Event == AdministratorAuthorityEvent.InstallerWindowOpened)
            .ToArrayAsync();

        var anchor = Assert.Single(anchors);
        Assert.Equal(T0, anchor.OccurredAtUtc);

        // And the window really is closed on day 20, which is the whole point of not moving the anchor.
        Assert.Equal(
            AdministratorAuthorityOutcome.WindowExpired,
            (await AuthorityAt(T0.AddDays(20))
                .EstablishByInstallerAsync(Candidate(), TimeSpan.FromHours(24))).Outcome);
    }

    [Fact]
    public async Task A_future_expiry_that_would_empty_the_tenant_is_refused()
    {
        // TrySetExpiry folded the trial at `now`, so SetExpiry(last, now + 1s) folded to ONE usable
        // administrator and was APPLIED — and a second later the installation had zero administrators, with
        // no event marking the moment and no path back except recovery. Clause 7 is about the state the
        // write LEADS TO, not the state one microsecond after it.
        var authority = AuthorityAt(T0);
        var candidate = Candidate();
        await authority.EstablishByInstallerAsync(candidate, installerWindow: null);

        var result = await authority.SetExpiryAsync(
            candidate.TeamId, candidate.PartyId, T0.AddSeconds(1), "test");

        Assert.Equal(AdministratorAuthorityOutcome.RefusedLastUsableAdministrator, result.Outcome);
        Assert.Single(await AuthorityAt(T0.AddDays(1)).UsableAsync());
    }

    [Fact]
    public async Task An_expiry_is_allowed_when_someone_outlasts_it()
    {
        // The invariant is a floor, not a freeze. A second administrator with no expiry of its own survives
        // every instant the trial fold examines, so date-bounding the first is permitted.
        var authority = AuthorityAt(T0);
        var first = Candidate("os:first#aaaa");
        await authority.EstablishByInstallerAsync(first, installerWindow: null);
        await SeedSecondAdministratorAsync(Candidate("os:second#bbbb"));

        var result = await authority.SetExpiryAsync(
            first.TeamId, first.PartyId, T0.AddDays(1), "test");

        Assert.True(result.Applied);
        var remaining = Assert.Single(await AuthorityAt(T0.AddDays(2)).UsableAsync());
        Assert.Equal("os:second#bbbb", remaining.PartyId);
    }

    // ── clause 5 + 7, PER TEAM ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_tenant_can_be_established_while_the_first_has_an_administrator()
    {
        // The gate counted across ALL teams, so team 1's administrator closed the gate for every other team
        // and team 2 could never be given one — by the installer OR by recovery. Authority is team-scoped;
        // the gate has to be too.
        var authority = AuthorityAt(T0);
        await authority.EstablishByInstallerAsync(Candidate(), installerWindow: null);

        var secondTeam = Candidate("os:second#bbbb") with
        {
            TeamId = "22222222-2222-2222-2222-222222222222",
        };
        var result = await RecoverAtAsync(T0, secondTeam);

        Assert.True(result.Applied);
        Assert.Equal(2, (await authority.UsableAsync()).Count);
    }

    [Fact]
    public async Task Removing_a_tenants_last_administrator_is_refused_even_when_another_tenant_has_one()
    {
        // The invariant counted globally too, so with administrators in teams T and U, revoking T's last
        // one was APPLIED because the global count was 2 — leaving team T permanently authority-less while
        // the check reported that it had held.
        var authority = AuthorityAt(T0);
        var teamT = Candidate("os:t#aaaa");
        await authority.EstablishByInstallerAsync(teamT, installerWindow: null);
        var teamU = Candidate("os:u#bbbb") with { TeamId = "22222222-2222-2222-2222-222222222222" };
        await SeedSecondAdministratorAsync(teamU);
        Assert.Equal(2, (await authority.UsableAsync()).Count);

        var result = await authority.AppendRemovalAsync(
            teamT.TeamId, teamT.PartyId, AdministratorAuthorityEvent.Revoked, "test");

        Assert.Equal(AdministratorAuthorityOutcome.RefusedLastUsableAdministrator, result.Outcome);
        Assert.Contains(await authority.UsableAsync(), administrator =>
            string.Equals(administrator.PartyId, teamT.PartyId, StringComparison.Ordinal));
    }

    // ── the chain is VERIFIED, not merely computed ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_forged_row_breaks_the_chain_and_every_write_except_recovery_refuses()
    {
        // ComputeHash had exactly one production caller — the writer — so the chain was decoration: a row
        // inserted by direct SQL with recovery provenance folded in as a usable administrator undetected.
        var authority = AuthorityAt(T0);
        var first = Candidate("os:first#aaaa");
        await authority.EstablishByInstallerAsync(first, installerWindow: null);
        await SeedSecondAdministratorAsync(Candidate("os:forged#bbbb"), breakTheChain: true);

        Assert.False(await authority.ChainVerifiesAsync());
        Assert.Equal(
            AdministratorAuthorityOutcome.RefusedBrokenChain,
            (await authority.AppendRemovalAsync(
                first.TeamId, first.PartyId, AdministratorAuthorityEvent.Revoked, "test")).Outcome);
        Assert.Equal(
            AdministratorAuthorityOutcome.RefusedBrokenChain,
            (await authority.SetExpiryAsync(first.TeamId, first.PartyId, T0.AddDays(1), "test")).Outcome);

        // Recovery is deliberately EXEMPT: a broken chain is one of the states recovery exists to get out
        // of, so refusing there would turn the detector into the brick.
        var rescueTeam = Candidate("os:rescued#cccc") with
        {
            TeamId = "33333333-3333-3333-3333-333333333333",
        };
        Assert.True((await RecoverAtAsync(T0, rescueTeam)).Applied);
    }

    // ── clause 7: the last usable administrator cannot be removed, on ANY path ───────────────────────────

    [Theory]
    [InlineData(AdministratorAuthorityEvent.Revoked)]
    [InlineData(AdministratorAuthorityEvent.Disabled)]
    [InlineData(AdministratorAuthorityEvent.PrincipalDeleted)]
    [InlineData(AdministratorAuthorityEvent.Demoted)]
    [InlineData(AdministratorAuthorityEvent.ReplacedByProjection)]
    public async Task Every_removal_path_refuses_the_last_usable_administrator(
        AdministratorAuthorityEvent removal)
    {
        var authority = AuthorityAt(T0);
        var candidate = Candidate();
        await authority.EstablishByInstallerAsync(candidate, installerWindow: null);

        var result = await authority.AppendRemovalAsync(
            candidate.TeamId, candidate.PartyId, removal, "test");

        Assert.Equal(AdministratorAuthorityOutcome.RefusedLastUsableAdministrator, result.Outcome);
        Assert.Equal(NodeAdministratorAuthority.LastUsableAdministratorCode, result.Code);
        Assert.Single(await authority.UsableAsync());
    }

    [Fact]
    public async Task Expiring_the_last_usable_administrator_is_refused()
    {
        // ADR 0066 clause 7 names "expire" as a removal path in its own right, and Microsoft Entra's
        // equivalent invariant is documented as holed at exactly the path that disables rather than deletes.
        var authority = AuthorityAt(T0);
        var candidate = Candidate();
        await authority.EstablishByInstallerAsync(candidate, installerWindow: null);

        var result = await authority.SetExpiryAsync(
            candidate.TeamId, candidate.PartyId, T0.AddMinutes(-1), "test");

        Assert.Equal(AdministratorAuthorityOutcome.RefusedLastUsableAdministrator, result.Outcome);
        Assert.Single(await authority.UsableAsync());
    }

    [Fact]
    public async Task A_removal_is_allowed_once_a_second_usable_administrator_exists()
    {
        // The invariant is a floor, not a freeze: it must not make administrators unremovable in general.
        var authority = AuthorityAt(T0);
        var first = Candidate("os:first#aaaa");
        await authority.EstablishByInstallerAsync(first, installerWindow: null);
        await SeedSecondAdministratorAsync(Candidate("os:second#bbbb"));

        var result = await authority.AppendRemovalAsync(
            first.TeamId, first.PartyId, AdministratorAuthorityEvent.Revoked, "test");

        Assert.True(result.Applied);
        var remaining = Assert.Single(await authority.UsableAsync());
        Assert.Equal("os:second#bbbb", remaining.PartyId);
    }

    [Fact]
    public async Task An_expired_administrator_does_not_count_as_usable()
    {
        // "Usable" excludes expired, so an expired administrator cannot satisfy the floor for a removal of
        // the only live one — the hole that turns a two-administrator install into a zero-administrator one.
        var authority = AuthorityAt(T0);
        var live = Candidate("os:live#aaaa");
        await authority.EstablishByInstallerAsync(live, installerWindow: null);
        await SeedSecondAdministratorAsync(Candidate("os:expired#bbbb"), expiresAt: T0.AddMinutes(-1));

        Assert.Single(await authority.UsableAsync());
        var result = await authority.AppendRemovalAsync(
            live.TeamId, live.PartyId, AdministratorAuthorityEvent.Revoked, "test");

        Assert.Equal(AdministratorAuthorityOutcome.RefusedLastUsableAdministrator, result.Outcome);
    }

    // ── clause 8: recovery is distinct, and never a re-arm of the installer ──────────────────────────────

    [Fact]
    public async Task Recovery_establishes_with_recovery_provenance_when_no_administrator_is_usable()
    {
        var authority = AuthorityAt(T0);

        var result = await RecoverAtAsync(T0, Candidate());

        Assert.True(result.Applied);
        var administrator = Assert.Single(await authority.UsableAsync());
        Assert.Equal(AdministratorProvenance.Recovery, administrator.Provenance);
        Assert.NotEqual(AdministratorProvenance.Bootstrap, administrator.Provenance);
    }

    [Fact]
    public async Task Recovery_does_not_re_arm_the_installer()
    {
        // THE clause-8 property. Establish by installer, then force the state back to "no usable
        // administrator" by every means the model allows, then prove the installer STILL cannot mint —
        // only recovery can. Without the append-only seal, deleting the last administrator would restore
        // bootstrap, which is a privilege-escalation path, not a recovery.
        var authority = AuthorityAt(T0);
        var first = Candidate("os:first#aaaa");
        await authority.EstablishByInstallerAsync(first, installerWindow: null);
        Assert.True(await authority.InstallerHasRunAsync());

        // Drop to zero usable administrators the only way the invariant permits: add a second, remove the
        // first, then expire the second out of usability.
        var second = Candidate("os:second#bbbb");
        await SeedSecondAdministratorAsync(second);
        await authority.AppendRemovalAsync(
            first.TeamId, first.PartyId, AdministratorAuthorityEvent.Revoked, "test");
        var later = AuthorityAt(T0.AddDays(30));
        await SeedSecondAdministratorAsync(second, expiresAt: T0.AddDays(1));
        Assert.Empty(await later.UsableAsync());

        // The installer's state gate is now OPEN (no usable administrator) — and it still refuses, because
        // the seal is a fact about the append-only history, not about the current state.
        var reMint = await later.EstablishByInstallerAsync(
            Candidate("os:attacker#cccc"), installerWindow: null);
        Assert.Equal(AdministratorAuthorityOutcome.InstallerSealed, reMint.Outcome);
        Assert.Empty(await later.UsableAsync());

        // Recovery is the only remaining path, and using it does not clear the seal either.
        Assert.True((await RecoverAtAsync(T0.AddDays(30), Candidate("os:rescued#dddd"))).Applied);
        Assert.True(await later.InstallerHasRunAsync());
        Assert.Equal(
            AdministratorProvenance.Recovery,
            Assert.Single(await later.UsableAsync()).Provenance);
    }

    [Fact]
    public async Task Recovery_refuses_while_a_usable_administrator_exists()
    {
        // Recovery is a recovery, not a second way to add administrators outside the grant path.
        var authority = AuthorityAt(T0);
        await authority.EstablishByInstallerAsync(Candidate(), installerWindow: null);

        var result = await RecoverAtAsync(T0, Candidate("os:other#cccc"));

        Assert.Equal(AdministratorAuthorityOutcome.AlreadyEstablished, result.Outcome);
        Assert.Single(await authority.UsableAsync());
    }

    // ── the log is append-only and hash-chained ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_authority_log_is_hash_chained_from_a_zero_root()
    {
        var authority = AuthorityAt(T0);
        var first = Candidate("os:first#aaaa");
        await authority.EstablishByInstallerAsync(first, installerWindow: null);
        await SeedSecondAdministratorAsync(Candidate("os:second#bbbb"));
        await authority.AppendRemovalAsync(
            first.TeamId, first.PartyId, AdministratorAuthorityEvent.Revoked, "test");

        await using var context = await _contexts.CreateDbContextAsync();
        var log = await context.AdministratorAuthority.AsNoTracking()
            .OrderBy(record => record.Sequence).ToArrayAsync();

        // Anchor, establishment, seeded second administrator, revocation — the window anchor is chained
        // like everything else, which is what makes moving it detectable.
        Assert.Equal(4, log.Length);
        Assert.Equal(AdministratorAuthorityRecord.ZeroHash, log[0].PreviousHash);
        Assert.Equal(AdministratorAuthorityEvent.InstallerWindowOpened, log[0].Event);
        for (var i = 0; i < log.Length; i++)
        {
            Assert.Equal(i + 1, log[i].Sequence);
            Assert.Equal(AdministratorAuthorityRecord.ComputeHash(log[i]), log[i].Hash);
            if (i > 0)
            {
                Assert.Equal(log[i - 1].Hash, log[i].PreviousHash);
            }
        }

        // And the same walk expressed as the production check, which now has a production caller.
        Assert.True(AdministratorAuthorityRecord.VerifyChain(log));

        // A removal APPENDS. The establishing event — the permanent audit of provenance — is still there.
        Assert.Contains(log, record =>
            record.Event == AdministratorAuthorityEvent.Established &&
            record.Provenance == AdministratorProvenance.Bootstrap);
    }

    /// <summary>
    /// Add a second administrator directly, bypassing the installer gate. Real second administrators arrive
    /// through the roster's admit path, which migration step 3 routes through one issuance path; step 1 only
    /// needs the invariant to see more than one usable administrator.
    /// </summary>
    /// <param name="candidate">The administrator to seed.</param>
    /// <param name="expiresAt">An optional expiry to write onto the seeded record.</param>
    /// <param name="breakTheChain">
    /// Write the row the way an attacker with direct SQL access would: correct-looking content, a hash that
    /// does not belong to the chain. This is the shape the chain existed to detect and never did.
    /// </param>
    private async Task SeedSecondAdministratorAsync(
        AdministratorCandidate candidate,
        DateTimeOffset? expiresAt = null,
        bool breakTheChain = false)
    {
        await using var context = await _contexts.CreateDbContextAsync();
        var tip = await context.AdministratorAuthority.AsNoTracking()
            .OrderByDescending(record => record.Sequence).FirstOrDefaultAsync();
        var record = new AdministratorAuthorityRecord
        {
            Sequence = (tip?.Sequence ?? 0) + 1,
            TeamId = candidate.TeamId,
            PartyId = candidate.PartyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = AdministratorProvenance.Recovery,
            MemberPublicKey = candidate.MemberPublicKey,
            AdmissionSignature = candidate.AdmissionSignature,
            AdmittedByPublicKey = candidate.AdmittedByPublicKey,
            AdmittedByPartyId = candidate.AdmittedByPartyId,
            IsGenesisAdmission = false,
            OccurredAtUtc = T0,
            ExpiresAtUtc = expiresAt,
            Reason = "test-seed",
            PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash,
            Hash = string.Empty,
        };
        record.Hash = breakTheChain
            ? new string('f', 64)
            : AdministratorAuthorityRecord.ComputeHash(record);
        context.AdministratorAuthority.Add(record);
        await context.SaveChangesAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
