using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// T-1005 (#372) mutation follow-up: behaviour of <see cref="WebTenantSelectionAuthority"/> that the
/// hosted mutation run showed was executed but not asserted — the durable-row validation clauses, the
/// challenge state gates on both sides of the audit, the abort and lease paths, and the exact owner
/// versions, actor and lease resource the completed selection leaves behind. Every test drives the
/// real authority over real SQLite identity and session stores through the existing fixture.
/// </summary>
public sealed partial class WebTenantSelectionAuthorityTests
{
    private const string AbortedSelectionFailureCode = "identity.session_selection_aborted";

    [Theory]
    [InlineData("command-type", "identity.session_selection_payload_invalid: command type is not admitted.")]
    [InlineData("schema-version", "identity.session_selection_payload_invalid: command type is not admitted.")]
    [InlineData("payload-missing", "identity.session_selection_payload_invalid: authority payload is missing.")]
    [InlineData("tenant-list-null", "identity.session_selection_payload_invalid: durable evidence was changed.")]
    [InlineData("tenant-list-two", "identity.session_selection_payload_invalid: durable evidence was changed.")]
    [InlineData("authority-digest", "identity.session_selection_payload_invalid: durable evidence was changed.")]
    [InlineData("fingerprint", "identity.session_selection_payload_invalid: durable evidence was changed.")]
    public async Task A_stored_selection_with_one_tampered_field_is_refused_with_its_exact_reason(
        string tamper,
        string expectedMessage)
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        Assert.NotNull(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        InstallationIdentityCoordinatorRecord row;
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            row = await identity.Coordinators.AsNoTracking().SingleAsync();
        }
        Assert.Equal(new[] { fixture.TenantId }, WebTenantSelectionAuthority.ValidateStoredSelection(row));

        switch (tamper)
        {
            case "command-type":
                row.CommandType = WebTenantSwitchAuthority.CommandType;
                break;
            case "schema-version":
                row.PayloadSchemaVersion = WebTenantSelectionAuthority.PayloadSchemaVersion + 1;
                break;
            case "payload-missing":
                row.IntentPayloadJson = "null";
                break;
            case "tenant-list-null":
                row.TenantIdsJson = "null";
                break;
            case "tenant-list-two":
                row.TenantIdsJson = $"[\"{fixture.TenantId}\",\"{fixture.TenantId}\"]";
                break;
            case "authority-digest":
                row.AuthorityEvidenceDigest = new string('0', 64);
                break;
            case "fingerprint":
                row.CommandFingerprint = new string('0', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        // Exactly InvalidOperationException: a null-dereference from a missing tenant list is not a refusal.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebTenantSelectionAuthority.ValidateStoredSelection(row));
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task A_missing_challenge_handle_is_refused_before_any_authority_is_read()
    {
        await using var fixture = await SelectionFixture.CreateAsync();

        Assert.Null(await fixture.Authority.SelectAsync(null, fixture.TenantId));

        Assert.Equal(0, fixture.Candidates.ListCalls);
        Assert.Equal(0, fixture.Resolver.ResolveCalls);
        await AssertRefusalHasNoExternalEffectsAsync(fixture);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("consumed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task An_unusable_challenge_is_refused_before_any_authority_is_resolved(string state)
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        var handle = fixture.ChallengeHandle;
        switch (state)
        {
            case "unknown":
                handle = "unknown-challenge-handle-with-at-least-256-bits-of-fixture-entropy";
                break;
            case "consumed":
                await ReplaceChallengeAsync(fixture, row => row with { ConsumedAtUtc = Now });
                break;
            case "revoked":
                await ReplaceChallengeAsync(fixture, row => row with { RevokedAtUtc = Now });
                break;
            case "expired":
                // The absolute TTL boundary itself is expired (IsExpired is at-or-after); the row keeps
                // its issued-before-expiry check constraint.
                await ReplaceChallengeAsync(fixture, ExpireAtNow);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        Assert.Null(await fixture.Authority.SelectAsync(handle, fixture.TenantId));

        // Refused at the challenge gate: no candidate listing, no partition, no home row, no session.
        Assert.Equal(0, fixture.Candidates.ListCalls);
        Assert.Equal(0, fixture.Resolver.ResolveCalls);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
        }
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
    }

    /// <summary>
    /// The membership moves (owner version) after the tenant head is prepared. The re-resolved
    /// authority is present but different, so the home aborts with its durable failure code and one
    /// owner-version step, the tenant prepare is aborted, and nothing is minted. A later retry under
    /// the original authority finds the Aborted home and refuses without even taking the lease.
    /// </summary>
    [Fact]
    public async Task A_membership_change_after_prepare_aborts_the_home_and_a_retry_refuses_without_a_lease()
    {
        var leases = new AlwaysLeaseCoordinator();
        await using var fixture = await SelectionFixture.CreateAsync(leases);
        fixture.Store.AfterPrepare = () =>
        {
            fixture.Store.MembershipView = membership =>
                membership with { OwnerVersion = membership.OwnerVersion + 1 };
            return Task.CompletedTask;
        };

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.True(fixture.Store.Aborted);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Aborted, home.State);
            Assert.Equal(AbortedSelectionFailureCode, home.FailureCode);
            Assert.Equal(2, home.OwnerVersion);
            Assert.Equal("[]", home.FinalReceiptsJson);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);

        fixture.Store.MembershipView = null;
        var acquires = leases.AcquireCalls;

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.Equal(acquires, leases.AcquireCalls);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Aborted, home.State);
            Assert.Equal(2, home.OwnerVersion);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    /// <summary>
    /// The audit completes, then (before the mint) the challenge is consumed, revoked or expired, or
    /// the account's security version moves. The mint re-reads both and refuses, so the Completed home
    /// leaves no session behind.
    /// </summary>
    [Theory]
    [InlineData("consumed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("account-security-version")]
    public async Task A_challenge_or_account_change_after_tenant_finalization_mints_no_session(string change)
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        fixture.Store.AfterFinalize = change switch
        {
            "consumed" => () => ReplaceChallengeAsync(fixture, row => row with { ConsumedAtUtc = Now }),
            "revoked" => () => ReplaceChallengeAsync(fixture, row => row with { RevokedAtUtc = Now }),
            "expired" => () => ReplaceChallengeAsync(fixture, ExpireAtNow),
            "account-security-version" => () => BumpAccountSecurityVersionAsync(fixture.IdentityFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Completed,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await sessions.AntiforgeryStates.AsNoTracking().ToArrayAsync());
    }

    /// <summary>
    /// A Completed selection whose session already exists is not minted twice, even when the challenge
    /// row is (re)presented as unconsumed.
    /// </summary>
    [Fact]
    public async Task A_completed_selection_with_a_minted_session_is_not_minted_again()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        Assert.NotNull(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        await ReplaceChallengeAsync(fixture, row => row with { ConsumedAtUtc = null });

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Single(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        Assert.Null((await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    public async Task A_refused_membership_lease_leaves_the_home_preparing_and_writes_no_tenant_head()
    {
        var leases = new AlwaysLeaseCoordinator { DenyAcquire = true };
        await using var fixture = await SelectionFixture.CreateAsync(leases);

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.Equal(1, leases.AcquireCalls);
        Assert.Equal(0, leases.ReleaseCalls);
        Assert.Equal(0, fixture.Store.WriteCalls);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Preparing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    [Fact]
    public async Task Recovery_without_the_membership_lease_leaves_a_committing_home_untouched()
    {
        var leases = new AlwaysLeaseCoordinator();
        await using var fixture = await SelectionFixture.CreateAsync(leases);
        var home = await InterruptAfterTenantFinalizationAsync(fixture);
        var finalizeCalls = fixture.Store.FinalizeCalls;
        leases.DenyAcquire = true;
        var (authority, _) = fixture.Restart();

        await ((IInstallationIdentityHomeRecovery)authority).RecoverAsync(home, CancellationToken.None);

        Assert.Equal(finalizeCalls, fixture.Store.FinalizeCalls);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Committing,
            (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
    }

    /// <summary>
    /// The home row is changed while the authority waits for the membership lease. The row reloaded
    /// under the lease is validated again, by both the live select and the recovery arm.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_home_reloaded_under_the_lease_is_validated_again(bool recovery)
    {
        var leases = new AlwaysLeaseCoordinator();
        await using var fixture = await SelectionFixture.CreateAsync(leases);
        Func<Task> tamper = () => UpdateHomeAsync(
            fixture,
            row => row.TenantIdsJson = $"[\"{fixture.UnusableTenantId}\"]");

        InvalidOperationException exception;
        if (recovery)
        {
            var home = await InterruptAfterTenantFinalizationAsync(fixture);
            var (authority, _) = fixture.Restart();
            leases.BeforeAcquire = tamper;
            exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ((IInstallationIdentityHomeRecovery)authority).RecoverAsync(home, CancellationToken.None));
        }
        else
        {
            leases.BeforeAcquire = tamper;
            exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        }

        Assert.StartsWith("identity.session_selection_payload_invalid:", exception.Message);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
    }

    /// <summary>
    /// The completed selection: the home stepped Preparing→Committing→Finalizing→Completed (owner
    /// version 4), the installation audit head stepped once, the envelope names the installation
    /// account as actor, and the only lease taken was the tenant's membership resource.
    /// </summary>
    [Fact]
    public async Task A_completed_selection_records_its_owner_versions_audit_actor_and_lease_resource()
    {
        var leases = new AlwaysLeaseCoordinator();
        await using var fixture = await SelectionFixture.CreateAsync(leases);
        var headBefore = await AuditHeadAsync(fixture.IdentityFactory);

        Assert.NotNull(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var home = await identity.Coordinators.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
        Assert.Equal(4, home.OwnerVersion);
        Assert.Null(home.FailureCode);
        var head = await AuditHeadAsync(fixture.IdentityFactory);
        Assert.Equal(headBefore.OwnerVersion + 1, head.OwnerVersion);
        Assert.Equal(headBefore.Sequence + 1, head.Sequence);
        var envelope = Assert.Single(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        Assert.Equal("installation-account", envelope.ActorKind);
        Assert.Equal(home.AccountId, envelope.ActorId);
        Assert.Equal(head.Sequence, envelope.Sequence);
        Assert.Equal(head.HeadHash, envelope.EnvelopeHash);
        Assert.Equal(new[] { $"identity.membership:{fixture.TenantId}" }, leases.AcquiredResources);
    }

    [Fact]
    public async Task Recovery_leases_the_tenant_membership_resource_before_rolling_a_selection_forward()
    {
        var leases = new AlwaysLeaseCoordinator();
        await using var fixture = await SelectionFixture.CreateAsync(leases);
        var home = await InterruptAfterTenantFinalizationAsync(fixture);
        var (authority, _) = fixture.Restart();

        await ((IInstallationIdentityHomeRecovery)authority).RecoverAsync(home, CancellationToken.None);

        var resource = $"identity.membership:{fixture.TenantId}";
        Assert.Equal(new[] { resource, resource }, leases.AcquiredResources);
        Assert.Single(await CompletedEnvelopesAsync(fixture.IdentityFactory));
    }

    [Theory]
    [InlineData("document-owner-version")]
    [InlineData("audit-sequence")]
    public async Task A_selection_receipt_with_a_zero_version_is_refused_before_the_home_records_it(string field)
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        if (field == "document-owner-version")
        {
            fixture.Store.ReceiptDocumentOwnerVersion = 0;
        }
        else
        {
            fixture.Store.ReceiptAuditSequence = 0;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.StartsWith("identity.session_selection_receipt_invalid:", exception.Message);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Committing, home.State);
            Assert.Equal("[]", home.FinalReceiptsJson);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    [Fact]
    public async Task Tenant_receipt_evidence_that_changes_after_it_was_recorded_is_refused()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        fixture.Store.DriftReceiptAfterFirstFinalize = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.Equal(
            "identity.session_selection_receipt_mismatch: tenant receipt evidence changed.",
            exception.Message);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Finalizing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    [Fact]
    public async Task Completion_refuses_an_installation_audit_whose_root_epoch_is_not_active()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        fixture.Store.AfterFinalize = () => RetireActiveRootEpochAsync(fixture.IdentityFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.Equal(
            "identity.session_selection_audit_invalid: installation audit is not authoritative.",
            exception.Message);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Finalizing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task A_completed_home_without_exactly_one_tenant_receipt_is_refused(string receiptsJson)
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        Assert.NotNull(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        await UpdateHomeAsync(fixture, row => row.FinalReceiptsJson = receiptsJson);
        await ReplaceChallengeAsync(fixture, row => row with { ConsumedAtUtc = null });

        // Exactly InvalidOperationException: an index or null fault is not the receipt refusal.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.Equal(
            "identity.session_selection_receipt_invalid: one tenant receipt is required.",
            exception.Message);
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Single(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
    }

    /// <summary>
    /// A durable home whose fingerprint was changed is refused where the existing home is read, not
    /// quietly treated as a different command.
    /// </summary>
    [Fact]
    public async Task A_retry_over_a_home_with_a_changed_fingerprint_is_refused_where_the_home_is_read()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        await InterruptAfterTenantFinalizationAsync(fixture);
        await UpdateHomeAsync(fixture, row => row.CommandFingerprint = new string('0', 64));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.StartsWith("identity.session_selection_payload_invalid:", exception.Message);
        await AssertNoSessionAndChallengeUnconsumedAsync(fixture);
    }

    private static async Task<InstallationIdentityCoordinatorRecord> InterruptAfterTenantFinalizationAsync(
        SelectionFixture fixture)
    {
        fixture.Store.ThrowAfterFinalizeOnce = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var home = await identity.Coordinators.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCoordinatorState.Committing, home.State);
        return home;
    }

    private static async Task AssertNoSessionAndChallengeUnconsumedAsync(SelectionFixture fixture)
    {
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        Assert.Null((await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync()).ConsumedAtUtc);
    }

    private static async Task ReplaceChallengeAsync(
        SelectionFixture fixture,
        Func<WebAccountAccessChallengeRecord, WebAccountAccessChallengeRecord> change)
    {
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        var challenge = await sessions.AccountAccessChallenges.SingleAsync();
        sessions.Entry(challenge).CurrentValues.SetValues(
            change(challenge) with { OwnerVersion = challenge.OwnerVersion + 1 });
        await sessions.SaveChangesAsync();
    }

    private static WebAccountAccessChallengeRecord ExpireAtNow(WebAccountAccessChallengeRecord row) =>
        row with { IssuedAtUtc = Now.AddMinutes(-10), AbsoluteExpiresAtUtc = Now };

    private static async Task UpdateHomeAsync(
        SelectionFixture fixture,
        Action<InstallationIdentityCoordinatorRecord> change)
    {
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var home = await identity.Coordinators.SingleAsync();
        change(home);
        await identity.SaveChangesAsync();
    }

    private static async Task BumpAccountSecurityVersionAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> factory)
    {
        await using var identity = await factory.CreateDbContextAsync();
        var account = await identity.Accounts.SingleAsync();
        account.SecurityVersion++;
        account.OwnerVersion++;
        await identity.SaveChangesAsync();
    }

    private static async Task RetireActiveRootEpochAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> factory)
    {
        await using var identity = await factory.CreateDbContextAsync();
        var installation = await identity.InstallationIdentities.AsNoTracking().SingleAsync();
        var root = await identity.RootKeyEpochs.SingleAsync(item =>
            item.InstallationIdentityId == installation.InstallationIdentityId &&
            item.EpochNumber == installation.ActiveRootEpoch);
        root.Status = InstallationRootEpochStatus.Retired;
        await identity.SaveChangesAsync();
    }

    private static async Task<InstallationAuditHeadRecord> AuditHeadAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> factory)
    {
        await using var identity = await factory.CreateDbContextAsync();
        var installation = await identity.InstallationIdentities.AsNoTracking().SingleAsync();
        return await identity.AuditHeads.AsNoTracking().SingleAsync(item =>
            item.InstallationIdentityId == installation.InstallationIdentityId);
    }
}
