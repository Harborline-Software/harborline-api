using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Session;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// T-1005 (#372) mutation follow-up: behaviour of <see cref="WebTenantSwitchAuthority"/> that the
/// hosted mutation run showed was executed but not asserted — the durable-row validation clauses,
/// the abort after a post-prepare authority drift, lease order and release, the tenant-head finalize
/// order, the owner versions and audit actor of a completed switch, and every refusal the final
/// rotation makes. Every test drives the real authority over real SQLite identity and session stores
/// through the existing fixture.
/// </summary>
public sealed partial class WebTenantSwitchAuthorityTests
{
    private const string ReplacementHandle =
        "replacement-selected-handle-with-at-least-256-bits-of-fixture-entropy";

    [Theory]
    [InlineData("command-type", "identity.session_switch_payload_invalid: command type is not admitted.")]
    [InlineData("schema-version", "identity.session_switch_payload_invalid: command type is not admitted.")]
    [InlineData("payload-missing", "identity.session_switch_payload_invalid: switch payload is missing.")]
    [InlineData("tenant-list-null", "identity.session_switch_payload_invalid: durable evidence was changed.")]
    [InlineData("correlation", "identity.session_switch_payload_invalid: durable evidence was changed.")]
    [InlineData("authority-digest", "identity.session_switch_payload_invalid: durable evidence was changed.")]
    [InlineData("fingerprint", "identity.session_switch_payload_invalid: durable evidence was changed.")]
    [InlineData("security-version", "identity.session_switch_payload_invalid: durable evidence was changed.")]
    public void A_stored_switch_with_one_tampered_field_is_refused_with_its_exact_reason(
        string tamper,
        string expectedMessage)
    {
        var row = StoredSwitch(OldTenantId, TargetTenantId);
        Assert.Equal(TargetTenantId, WebTenantSwitchAuthority.ValidateStoredSwitch(row).Target.Membership.TenantId);

        switch (tamper)
        {
            case "command-type":
                row.CommandType = WebTenantSelectionAuthority.CommandType;
                break;
            case "schema-version":
                row.PayloadSchemaVersion = WebTenantSwitchAuthority.PayloadSchemaVersion + 1;
                break;
            case "payload-missing":
                row.IntentPayloadJson = "null";
                break;
            case "tenant-list-null":
                row.TenantIdsJson = "null";
                break;
            case "correlation":
                row.CorrelationId = new string('0', 64);
                break;
            case "authority-digest":
                row.AuthorityEvidenceDigest = new string('0', 64);
                break;
            case "fingerprint":
                row.CommandFingerprint = new string('0', 64);
                break;
            case "security-version":
                row.ExpectedAccountSecurityVersion++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        // Exactly InvalidOperationException: a null-argument fault from a missing tenant list is not a refusal.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebTenantSwitchAuthority.ValidateStoredSwitch(row));
        Assert.Equal(expectedMessage, exception.Message);
    }

    /// <summary>
    /// After both tenant heads are prepared, one coordinate of the switch's authority drifts: the
    /// account's security version, one field of the old tenant's membership (or the membership itself),
    /// or the target membership. The re-check aborts the home with its durable failure code and one
    /// owner-version step, aborts both tenant prepares, finalizes no head and rotates nothing.
    /// </summary>
    [Theory]
    [InlineData("account-security-version")]
    [InlineData("old-membership-missing")]
    [InlineData("old-membership-id")]
    [InlineData("old-owner-version")]
    [InlineData("old-principal")]
    [InlineData("old-grant-id")]
    [InlineData("old-grant-owner-version")]
    [InlineData("old-authorization-epoch")]
    [InlineData("target-owner-version")]
    public async Task An_authority_drift_after_prepare_aborts_the_switch_before_any_head_is_finalized(string drift)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetStore.AfterPrepare = async () =>
        {
            switch (drift)
            {
                case "account-security-version":
                    await BumpAccountAsync(fixture, account =>
                    {
                        account.SecurityVersion++;
                        account.OwnerVersion++;
                    });
                    break;
                case "old-membership-missing":
                    fixture.OldStore.MembershipView = _ => null;
                    break;
                case "old-membership-id":
                    fixture.OldStore.MembershipView = m => m with { MembershipId = "drifted-membership" };
                    break;
                case "old-owner-version":
                    fixture.OldStore.MembershipView = m => m with { OwnerVersion = m.OwnerVersion + 1 };
                    break;
                case "old-principal":
                    fixture.OldStore.MembershipView = m => m with { CanonicalPrincipalId = "drifted-principal" };
                    break;
                case "old-grant-id":
                    fixture.OldStore.MembershipView = m => m with { GrantId = "drifted-grant" };
                    break;
                case "old-grant-owner-version":
                    fixture.OldStore.MembershipView = m => m with { GrantOwnerVersion = m.GrantOwnerVersion + 1 };
                    break;
                case "old-authorization-epoch":
                    fixture.OldStore.MembershipView = m => m with { AuthorizationEpoch = m.AuthorizationEpoch + 1 };
                    break;
                case "target-owner-version":
                    fixture.TargetStore.MembershipView = m => m with { OwnerVersion = m.OwnerVersion + 1 };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(drift), drift, null);
            }
        };

        Assert.Null(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Aborted, home.State);
            Assert.Equal("identity.session_switch_aborted", home.FailureCode);
            Assert.Equal(2, home.OwnerVersion);
            Assert.Equal("[]", home.FinalReceiptsJson);
        }
        Assert.Equal(1, fixture.OldStore.AbortRevocationCalls);
        Assert.Equal(1, fixture.TargetStore.AbortSelectionCalls);
        Assert.Equal(0, fixture.OldStore.AbortSelectionCalls);
        Assert.Equal(0, fixture.TargetStore.AbortRevocationCalls);
        Assert.Empty(fixture.StoreLog);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    [Fact]
    public async Task A_disabled_account_is_refused_before_any_candidate_is_listed()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        await BumpAccountAsync(fixture, account => account.Status = InstallationAccountStatus.Disabled);

        Assert.Null(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(0, fixture.Candidates.ListCalls);
        await AssertNoHomeAndNothingRotatedAsync(fixture);
    }

    [Theory]
    [InlineData("unknown-handle")]
    [InlineData("same-tenant")]
    public async Task An_unknown_handle_or_a_switch_to_the_current_tenant_is_refused_first(string request)
    {
        await using var fixture = await SwitchFixture.CreateAsync();

        var switched = request == "unknown-handle"
            ? await fixture.Authority.SwitchAsync(ReplacementHandle, TargetTenantId)
            : await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, OldTenantId);

        Assert.Null(switched);
        Assert.Equal(0, fixture.Candidates.ListCalls);
        await AssertNoHomeAndNothingRotatedAsync(fixture);
    }

    [Fact]
    public async Task A_listed_target_without_a_usable_membership_is_refused_before_any_head()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetStore.MembershipView = _ => null;

        Assert.Null(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(1, fixture.Candidates.ListCalls);
        await AssertNoHomeAndNothingRotatedAsync(fixture);
    }

    /// <summary>
    /// The completed switch: home owner version 4 (Preparing→Committing→Finalizing→Completed), one
    /// audit-head step, the installation account as audit actor, leases taken in ordinal tenant order
    /// and released in reverse, tenant heads finalized old-then-target (commit, then the durable
    /// re-read), and a rotation that carries its own antiforgery state, a GUID-"N" revocation id and the
    /// unclamped idle expiry.
    /// </summary>
    [Fact]
    public async Task A_completed_switch_records_owner_versions_lease_and_head_order_and_rotation_shape()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        var headBefore = await AuditHeadAsync(fixture.IdentityFactory);

        var switched = await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId);

        Assert.NotNull(switched);
        InstallationIdentityCoordinatorRecord home;
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            home = await identity.Coordinators.AsNoTracking().SingleAsync();
        }
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

        Assert.Equal(
            new[]
            {
                $"acquire:identity.membership:{OldTenantId}",
                $"acquire:identity.membership:{TargetTenantId}",
                $"release:identity.membership:{TargetTenantId}",
                $"release:identity.membership:{OldTenantId}",
            },
            fixture.LeaseLog);
        Assert.Empty(fixture.OldLeases.HeldLeases);
        Assert.Empty(fixture.TargetLeases.HeldLeases);
        Assert.Equal(
            new[]
            {
                $"finalize-revocation:{OldTenantId}",
                $"finalize-selection:{TargetTenantId}",
                $"finalize-revocation:{OldTenantId}",
                $"finalize-selection:{TargetTenantId}",
            },
            fixture.StoreLog);

        await using var sessions = fixture.SessionFactory.CreateDbContext();
        var replacement = await sessions.UserSessions.AsNoTracking()
            .SingleAsync(row => row.CoordinationCorrelationId == home.CorrelationId);
        Assert.Equal(Now + new SessionOptions().IdleTimeout, replacement.IdleExpiresAtUtc);
        Assert.Equal(SwitchFixture.AbsoluteExpiry, replacement.AbsoluteExpiresAtUtc);
        var antiforgery = await sessions.AntiforgeryStates.AsNoTracking()
            .SingleAsync(row => row.AntiforgeryStateId == replacement.AntiforgeryStateId);
        Assert.Equal(WebCookieAudience.SelectedSession, antiforgery.Audience);
        Assert.Equal(replacement.SessionCorrelationId, antiforgery.SubjectCorrelationId);
        Assert.Equal(home.CorrelationId, antiforgery.CoordinationCorrelationId);
        var revocation = await sessions.Revocations.AsNoTracking().SingleAsync();
        Assert.Matches("^[0-9a-f]{32}$", revocation.RevocationId);
        Assert.Equal(WebCookieAudience.SelectedSession, revocation.Audience);
        Assert.Equal(home.CorrelationId, revocation.CoordinationCorrelationId);
    }

    [Fact]
    public async Task The_replacement_idle_expiry_is_clamped_to_the_old_absolute_expiry()
    {
        await using var fixture = await SwitchFixture.CreateAsync(sessionOptions: new SessionOptions
        {
            AbsoluteLifetime = TimeSpan.FromHours(24),
            IdleTimeout = TimeSpan.FromHours(12),
        });

        var switched = await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId);

        Assert.NotNull(switched);
        Assert.Equal(SwitchFixture.AbsoluteExpiry, switched!.ExpiresAtUtc);
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        var replacement = await sessions.UserSessions.AsNoTracking()
            .SingleAsync(row => row.TenantId == TargetTenantId);
        Assert.Equal(SwitchFixture.AbsoluteExpiry, replacement.IdleExpiresAtUtc);
    }

    /// <summary>
    /// The target tenant's lease is refused. The lease already held on the old tenant is released, the
    /// home stays Preparing, and the recovery arm backs off the same way without touching a head.
    /// </summary>
    [Fact]
    public async Task A_refused_second_lease_releases_the_first_for_the_switch_and_for_recovery()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetLeases.DenyAcquire = true;
        var refusal = new[]
        {
            $"acquire:identity.membership:{OldTenantId}",
            $"acquire:identity.membership:{TargetTenantId}",
            $"release:identity.membership:{OldTenantId}",
        };

        Assert.Null(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(refusal, fixture.LeaseLog);
        Assert.Empty(fixture.OldLeases.HeldLeases);
        InstallationIdentityCoordinatorRecord home;
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            home = await identity.Coordinators.AsNoTracking().SingleAsync();
        }
        Assert.Equal(InstallationIdentityCoordinatorState.Preparing, home.State);

        await ((IInstallationIdentityHomeRecovery)fixture.Authority).RecoverAsync(home, CancellationToken.None);

        Assert.Equal(refusal.Concat(refusal), fixture.LeaseLog);
        Assert.Empty(fixture.OldLeases.HeldLeases);
        Assert.Empty(fixture.StoreLog);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Preparing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// A durable switch exists for the old session's correlation, but the handle now presented is a
    /// different one (the session row was re-issued under the same correlation). The stored old-handle
    /// digest no longer matches, so the retry is refused and the home does not move.
    /// </summary>
    [Fact]
    public async Task A_retry_presenting_another_handle_for_the_old_correlation_is_refused()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        await InterruptTargetFinalizationAsync(fixture);
        await ReplaceOldSessionAsync(fixture, row => row with { HandleDigest = Digest(ReplacementHandle) });

        Assert.Null(await fixture.Authority.SwitchAsync(ReplacementHandle, TargetTenantId));

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// A Completed switch retried exactly at the old session's absolute expiry, or before it was issued,
    /// performs no rotation.
    /// </summary>
    [Theory]
    [InlineData("at-absolute-expiry")]
    [InlineData("before-issue")]
    public async Task A_completed_switch_retried_outside_the_old_session_lifetime_rotates_nothing(string when)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        await CompleteWithoutRotationAsync(fixture);
        var at = when == "at-absolute-expiry" ? SwitchFixture.AbsoluteExpiry : Now.AddMinutes(-10);
        var (retry, _) = fixture.Restart(at);

        Assert.Null(await retry.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("security-version")]
    public async Task A_completed_switch_retried_after_the_account_changed_rotates_nothing(string change)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        await CompleteWithoutRotationAsync(fixture);
        await BumpAccountAsync(fixture, account =>
        {
            if (change == "disabled")
            {
                account.Status = InstallationAccountStatus.Disabled;
            }
            else
            {
                account.SecurityVersion++;
            }
            account.OwnerVersion++;
        });
        var (retry, _) = fixture.Restart();

        Assert.Null(await retry.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// While the tenant heads finalize, the durable old-session row changes in exactly one authority
    /// coordinate (or is re-keyed, or deleted). The audit completes, but the final rotation compares the
    /// durable row with the session the switch started from and rotates nothing.
    /// </summary>
    [Theory]
    [InlineData("account-security-version")]
    [InlineData("tenant")]
    [InlineData("membership-id")]
    [InlineData("membership-owner-version")]
    [InlineData("principal")]
    [InlineData("party")]
    [InlineData("grant-pin")]
    [InlineData("authorization-epoch")]
    [InlineData("absolute-expiry")]
    [InlineData("session-correlation")]
    [InlineData("deleted")]
    public async Task A_durable_old_session_that_changed_during_the_switch_is_not_rotated(string change)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetStore.AfterFinalize = () => ReplaceOldSessionAsync(fixture, row => change switch
        {
            "account-security-version" => row with { AccountSecurityVersion = row.AccountSecurityVersion + 1 },
            "tenant" => row with { TenantId = "33333333-3333-3333-3333-333333333333" },
            "membership-id" => row with { MembershipId = "drifted-membership" },
            "membership-owner-version" => row with { MembershipOwnerVersion = row.MembershipOwnerVersion + 1 },
            "principal" => row with { TenantPrincipalId = "drifted-principal" },
            "party" => row with { CanonicalPartyReference = "drifted-party" },
            "grant-pin" => WithGrantPin(row, new PinnedGrantOwnerVersion("old-grant", 5)),
            "authorization-epoch" => row with { AuthorizationEpoch = row.AuthorizationEpoch + 1 },
            "absolute-expiry" => row with { AbsoluteExpiresAtUtc = Now.AddHours(7) },
            "session-correlation" => row with { SessionCorrelationId = "rekeyed-session" },
            "deleted" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        });

        Assert.Null(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        InstallationIdentityCoordinatorRecord home;
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            home = await identity.Coordinators.AsNoTracking().SingleAsync();
        }
        Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
        Assert.Single(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.False(await sessions.UserSessions.AsNoTracking()
            .AnyAsync(row => row.CoordinationCorrelationId == home.CorrelationId));
        Assert.Empty(await sessions.Revocations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await sessions.AntiforgeryStates.AsNoTracking().ToArrayAsync());
    }

    /// <summary>
    /// A revocation of another subject, or of another audience for the old subject, does not block
    /// the rotation: only a selected-session revocation of the old session itself does.
    /// </summary>
    [Theory]
    [InlineData("other-subject")]
    [InlineData("other-audience")]
    public async Task Rotation_ignores_a_revocation_of_another_subject_or_audience(string unrelated)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        var accountId = await AccountIdAsync(fixture);
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            sessions.Revocations.Add(new WebSessionRevocationRecord(
                RevocationId: Guid.NewGuid().ToString("N"),
                Audience: unrelated == "other-subject"
                    ? WebCookieAudience.SelectedSession
                    : WebCookieAudience.AccountChallenge,
                AccountId: accountId,
                SubjectCorrelationId: unrelated == "other-subject" ? "other-session" : "old-session",
                SupersededByCorrelationId: null,
                ReasonCode: "unrelated-fixture-revocation",
                CoordinationCorrelationId: "unrelated-fixture-revocation",
                RevokedAtUtc: Now.AddMinutes(-1),
                OwnerVersion: 1));
            await sessions.SaveChangesAsync();
        }

        var switched = await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId);

        Assert.NotNull(switched);
        await using var after = fixture.SessionFactory.CreateDbContext();
        Assert.Equal(3, await after.UserSessions.CountAsync());
        Assert.Equal(2, await after.Revocations.CountAsync());
        Assert.True(await after.Revocations.AsNoTracking().AnyAsync(row =>
            row.Audience == WebCookieAudience.SelectedSession &&
            row.SubjectCorrelationId == "old-session" &&
            row.ReasonCode == WebTenantSwitchAuthority.ReasonCode));
    }

    [Theory]
    [InlineData(true, "document-owner-version")]
    [InlineData(true, "audit-sequence")]
    [InlineData(false, "document-owner-version")]
    [InlineData(false, "audit-sequence")]
    public async Task A_tenant_receipt_with_a_zero_version_is_refused(bool oldTenantReceipt, string field)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        var store = oldTenantReceipt ? fixture.OldStore : fixture.TargetStore;
        if (field == "document-owner-version")
        {
            store.ReceiptDocumentOwnerVersion = 0;
        }
        else
        {
            store.ReceiptAuditSequence = 0;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.StartsWith("identity.session_switch_receipt_invalid:", exception.Message);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Committing, home.State);
            Assert.Equal("[]", home.FinalReceiptsJson);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// The target tenant's receipt changes after the home recorded it — on the Finalizing roll-forward
    /// and on a Completed retry. Both re-reads refuse with the mismatch reason and rotate nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_receipt_evidence_that_changes_after_it_was_recorded_is_refused(bool completed)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        var authority = fixture.Authority;
        var expectedState = InstallationIdentityCoordinatorState.Finalizing;
        if (completed)
        {
            await CompleteWithoutRotationAsync(fixture);
            authority = fixture.Restart().Authority;
            expectedState = InstallationIdentityCoordinatorState.Completed;
        }
        fixture.TargetStore.DriftReceiptAfterFirstFinalize = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(
            "identity.session_switch_receipt_mismatch: tenant receipt evidence changed.",
            exception.Message);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(expectedState, (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    [Fact]
    public async Task Completion_refuses_an_installation_audit_whose_root_epoch_is_not_active()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetStore.AfterFinalize = () => RetireActiveRootEpochAsync(fixture.IdentityFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(
            "identity.session_switch_audit_invalid: installation audit is not authoritative.",
            exception.Message);
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Finalizing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    [Fact]
    public async Task A_completed_home_whose_receipts_are_missing_is_refused_with_its_reason()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        await CompleteWithoutRotationAsync(fixture);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.SingleAsync();
            home.FinalReceiptsJson = "null";
            await identity.SaveChangesAsync();
        }
        var (retry, _) = fixture.Restart();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retry.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.Equal(
            "identity.session_switch_receipt_invalid: two tenant receipts are required.",
            exception.Message);
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// Recovering an already Completed switch re-finalizes no tenant head and releases both leases.
    /// </summary>
    [Fact]
    public async Task Recovery_of_a_completed_switch_touches_no_tenant_head_and_releases_its_leases()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        Assert.NotNull(await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));
        InstallationIdentityCoordinatorRecord home;
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            home = await identity.Coordinators.AsNoTracking().SingleAsync();
        }
        var heads = fixture.StoreLog.ToArray();
        var leases = fixture.LeaseLog.ToArray();

        await ((IInstallationIdentityHomeRecovery)fixture.Authority).RecoverAsync(home, CancellationToken.None);

        Assert.Equal(heads, fixture.StoreLog);
        Assert.Equal(
            leases.Concat(new[]
            {
                $"acquire:identity.membership:{OldTenantId}",
                $"acquire:identity.membership:{TargetTenantId}",
                $"release:identity.membership:{TargetTenantId}",
                $"release:identity.membership:{OldTenantId}",
            }),
            fixture.LeaseLog);
        Assert.Empty(fixture.OldLeases.HeldLeases);
        Assert.Empty(fixture.TargetLeases.HeldLeases);
        Assert.Single(await CompletedEnvelopesAsync(fixture.IdentityFactory));
    }

    private static async Task InterruptTargetFinalizationAsync(SwitchFixture fixture)
    {
        fixture.TargetStore.ThrowAfterFinalizeOnce = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Committing,
            (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
    }

    /// <summary>Interrupted finalization, then the recovery drain: Completed, old session still live.</summary>
    private static async Task CompleteWithoutRotationAsync(SwitchFixture fixture)
    {
        await InterruptTargetFinalizationAsync(fixture);
        await fixture.Restart().Recovery.RecoverPendingAsync();
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Completed,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    private static async Task AssertNothingRotatedAsync(SwitchFixture fixture, int expectedSessions)
    {
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Equal(expectedSessions, await sessions.UserSessions.CountAsync());
        Assert.Empty(await sessions.Revocations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await sessions.AntiforgeryStates.AsNoTracking().ToArrayAsync());
    }

    private static async Task AssertNoHomeAndNothingRotatedAsync(SwitchFixture fixture)
    {
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
        }
        Assert.Empty(fixture.StoreLog);
        Assert.Empty(fixture.LeaseLog);
        await AssertNothingRotatedAsync(fixture, expectedSessions: 2);
    }

    /// <summary>
    /// Deletes the durable "old-session" row and, unless <paramref name="change"/> returns null,
    /// writes its replacement (two saves, so a re-keyed row never collides with the tracked original).
    /// </summary>
    private static async Task ReplaceOldSessionAsync(
        SwitchFixture fixture,
        Func<WebUserSessionRecord, WebUserSessionRecord?> change)
    {
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        var row = await sessions.UserSessions.SingleAsync(item => item.SessionCorrelationId == "old-session");
        var replacement = change(row);
        sessions.UserSessions.Remove(row);
        await sessions.SaveChangesAsync();
        if (replacement is not null)
        {
            sessions.UserSessions.Add(replacement);
            await sessions.SaveChangesAsync();
        }
    }

    private static WebUserSessionRecord WithGrantPin(WebUserSessionRecord row, PinnedGrantOwnerVersion pin) =>
        new(
            SessionCorrelationId: row.SessionCorrelationId,
            AccountId: row.AccountId,
            AccountSecurityVersion: row.AccountSecurityVersion,
            TenantId: row.TenantId,
            MembershipId: row.MembershipId,
            MembershipOwnerVersion: row.MembershipOwnerVersion,
            TenantPrincipalId: row.TenantPrincipalId,
            CanonicalPartyReference: row.CanonicalPartyReference,
            PinnedGrantOwnerVersions: [pin],
            AuthorizationEpoch: row.AuthorizationEpoch,
            HandleDigest: row.HandleDigest,
            AntiforgeryStateId: row.AntiforgeryStateId,
            CoordinationCorrelationId: row.CoordinationCorrelationId,
            IssuedAtUtc: row.IssuedAtUtc,
            IdleExpiresAtUtc: row.IdleExpiresAtUtc,
            AbsoluteExpiresAtUtc: row.AbsoluteExpiresAtUtc,
            OwnerVersion: row.OwnerVersion);

    private static async Task BumpAccountAsync(SwitchFixture fixture, Action<InstallationAccountRecord> change)
    {
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var account = await identity.Accounts.SingleAsync();
        change(account);
        await identity.SaveChangesAsync();
    }

    private static async Task<string> AccountIdAsync(SwitchFixture fixture)
    {
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        return (await identity.Accounts.AsNoTracking().SingleAsync()).AccountId;
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
