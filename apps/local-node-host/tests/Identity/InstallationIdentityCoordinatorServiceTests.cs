using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class InstallationIdentityCoordinatorServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 13, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Tenant_Candidate_Classification_Returns_None_For_Zero_Usable_Memberships()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(0);
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId));
        var classification = InstallationTenantMembershipClassification.From(candidates);

        Assert.Empty(classification.Candidates);
        Assert.Null(classification.Selection);
    }

    [Fact]
    public async Task Tenant_Candidate_Classification_Returns_ForSingle_At_One_Membership()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId));
        var classification = InstallationTenantMembershipClassification.From(candidates);

        var single = Assert.IsType<TenantSelection.ForSingle>(classification.Selection);
        Assert.Equal(fixture.Tenants[0].TenantId, single.TenantId.Value);
        Assert.Equal(TenantMembershipStatus.Active, Assert.Single(candidates).MembershipStatus);
    }

    [Fact]
    public async Task Tenant_Candidate_Classification_Returns_ForMultiple_At_Two_Memberships()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId));
        var classification = InstallationTenantMembershipClassification.From(candidates);

        var multiple = Assert.IsType<TenantSelection.ForMultiple>(classification.Selection);
        Assert.Equal(2, multiple.TenantIds.Length);
        Assert.Equal(
            fixture.Tenants.Select(item => item.TenantId).Order(StringComparer.Ordinal),
            multiple.TenantIds.Select(item => item.Value));
    }

    [Fact]
    public async Task Tenant_Candidate_Locator_Isolates_Overlapping_Memberships_By_Account()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(3);
        var sharedTenant = fixture.Tenants[0];
        var accountAOnlyTenant = fixture.Tenants[1];
        var accountBOnlyTenant = fixture.Tenants[2];
        var accountA = new CoordinatorFixture.ActorVersion(
            fixture.AccountId,
            fixture.AccountOwnerVersion,
            fixture.AccountSecurityVersion);
        var accountB = await fixture.CreateActiveActorAsync();

        await fixture.Coordinator.ExecuteAsync(fixture.CommandForAccount(
            accountA,
            [sharedTenant, accountAOnlyTenant],
            "membership-command-account-a",
            "account-a-principal",
            "account-a-grant"));
        await fixture.Coordinator.ExecuteAsync(fixture.CommandForAccount(
            accountB,
            [sharedTenant, accountBOnlyTenant],
            "membership-command-account-b",
            "account-b-principal",
            "account-b-grant"));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var accountAReceipt = await context.Coordinators.SingleAsync(
                row => row.CorrelationId == "membership-command-account-a");
            accountAReceipt.TenantIdsJson = JsonSerializer.Serialize(
                new[] { accountAOnlyTenant.TenantId });
            await context.SaveChangesAsync();
        }
        Assert.NotNull(await sharedTenant.Authority.GetMembershipAsync(
            accountA.AccountId,
            CancellationToken.None));
        Assert.NotNull(await sharedTenant.Authority.GetMembershipAsync(
            accountB.AccountId,
            CancellationToken.None));
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(accountA.AccountId));

        Assert.Equal(accountAOnlyTenant.TenantId, Assert.Single(candidates).TenantId.Value);
        Assert.DoesNotContain(
            candidates,
            candidate => candidate.TenantId.Value == sharedTenant.TenantId);
        Assert.DoesNotContain(
            candidates,
            candidate => candidate.TenantId.Value == accountBOnlyTenant.TenantId);
    }

    [Fact]
    public async Task Tenant_Candidate_Locator_Fails_Closed_On_A_Corrupt_Completed_Receipt()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var completed = await context.Coordinators.SingleAsync();
            completed.TenantIdsJson = "{not-valid-json";
            await context.SaveChangesAsync();
        }
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId)));

        Assert.StartsWith("identity.tenant_candidate_receipt_invalid:", exception.Message);
    }

    [Fact]
    public async Task Tenant_Candidate_Locator_Filters_Completed_But_Revoked_Membership()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var activation = fixture.Command(fixture.Tenants);
        await fixture.Coordinator.ExecuteAsync(activation);
        var revocation = activation with
        {
            CorrelationId = "membership-revoke",
            Mutations = activation.Mutations.Select(mutation => mutation with
            {
                ExpectedMembershipOwnerVersion = 1,
                TargetStatus = TenantMembershipStatus.Revoked,
            }).ToArray(),
        };
        await fixture.Coordinator.ExecuteAsync(revocation);
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId));

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Tenant_Candidate_Locator_Filters_A_Stale_Authority_Admission()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        fixture.SetAdmission(new RejectExistingAdmission());
        var locator = new InstallationTenantCandidateLocator(fixture.HomeFactory, fixture.Coordinator);

        var candidates = await locator.ListForAccountAsync(new PrincipalUserId(fixture.AccountId));

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Multi_Tenant_Command_Completes_In_Canonical_Order_With_Audit_And_Replay_Evidence()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        var command = fixture.Command(fixture.Tenants.AsEnumerable().Reverse().ToArray());

        var result = await fixture.Coordinator.ExecuteAsync(command);

        Assert.Equal(InstallationIdentityCoordinationStatus.Completed, result.Status);
        Assert.Equal(
            fixture.Tenants.Select(item => item.TenantId).Order(StringComparer.Ordinal),
            result.Receipts.Select(item => item.TenantId));
        foreach (var tenant in fixture.Tenants)
        {
            Assert.NotNull(await tenant.Authority.GetMembershipAsync(
                fixture.AccountId, CancellationToken.None));
        }

        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var coordinator = await context.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, coordinator.State);
            Assert.Equal(2, await context.AuditEnvelopes.CountAsync());
            var completion = await context.AuditEnvelopes.AsNoTracking()
                .SingleAsync(item => item.Sequence == 2);
            Assert.Equal("TenantMembershipCoordinationCompleted", completion.EventType);
            Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(completion));
        }

        var replay = await fixture.Coordinator.ExecuteAsync(command);
        Assert.Equal(InstallationIdentityCoordinationStatus.IdempotentReplay, replay.Status);
        Assert.Equal(result.Receipts, replay.Receipts);

        var changedMutation = command.Mutations[0] with { CanonicalPrincipalId = "changed-principal" };
        var changed = await fixture.Coordinator.ExecuteAsync(command with
        {
            Mutations = [changedMutation, .. command.Mutations.Skip(1)],
        });
        Assert.Equal(InstallationIdentityCoordinationStatus.ChangedReplay, changed.Status);
    }

    [Fact]
    public async Task Precommit_Failure_Aborts_All_Prepared_Tenants_And_Leaves_No_Visible_Membership()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        var ordered = fixture.Tenants.OrderBy(item => item.TenantId, StringComparer.Ordinal).ToArray();
        ordered[1].Authority = new FailPrepareStore(ordered[1].Authority);

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(ordered));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.False(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        foreach (var tenant in ordered)
        {
            Assert.Null(await tenant.Authority.GetMembershipAsync(fixture.AccountId, CancellationToken.None));
            Assert.Equal(
                tenant == ordered[0] ? TenantMembershipIntentState.Aborted : null,
                await tenant.Authority.GetIntentStateAsync("membership-command", CancellationToken.None));
        }

        await using var context = fixture.HomeFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Aborted,
            (await context.Coordinators.AsNoTracking().SingleAsync()).State);
        Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Crash_After_Tenant_Finalization_Rolls_Forward_From_Durable_Tenant_Receipt()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.Tenants[0].Authority = new ThrowAfterFinalizeOnceStore(fixture.Tenants[0].Authority);

        var interrupted = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, interrupted.Status);
        Assert.True(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        Assert.NotNull(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
        Assert.Null(await fixture.Coordinator.ResolveUsableMembershipAsync(
            fixture.AccountId, fixture.Tenants[0].TenantId));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await context.Coordinators.AsNoTracking().SingleAsync()).State);
            Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
        }

        var recovery = new InstallationIdentityCoordinatorRecoveryService(
            fixture.HomeFactory,
            fixture.Coordinator);
        var recovered = Assert.Single(await recovery.RecoverPendingAsync());

        Assert.Equal(InstallationIdentityCoordinationStatus.Completed, recovered.Status);
        Assert.False(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        Assert.Single(recovered.Receipts);
        Assert.NotNull(await fixture.Coordinator.ResolveUsableMembershipAsync(
            fixture.AccountId, fixture.Tenants[0].TenantId));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Completed,
                (await context.Coordinators.AsNoTracking().SingleAsync()).State);
            Assert.Equal(2, await context.AuditEnvelopes.CountAsync());
        }
    }

    [Fact]
    public async Task Grant_Mutation_Waits_For_Completed_Audit_After_Previous_Window_Fault()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var tenant = fixture.Tenants[0];
        var grantId = GrantId.New();
        var tenantId = new TenantId(tenant.TenantId);
        var grant = new AccessGrant(grantId, tenantId, new ActorId(fixture.AccountId),
            AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(FixedNow), GranterKind.Person, new ActorId("founder"), FixedNow,
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId("founder")),
            FixedNow);
        await fixture.GrantStore.SaveAsync(tenantId, grant, expectedOwnerVersion: 0);
        var before = await fixture.GrantStore.FindAsync(tenantId, grantId);
        tenant.Authority = new ThrowBeforeFinalizeOnceStore(tenant.Authority);
        var command = fixture.Command(fixture.Tenants);
        command = command with
        {
            Mutations = [command.Mutations[0] with
            {
                CanonicalPrincipalId = fixture.AccountId,
                GrantId = grantId.ToString(),
                RequestedPermissions = [Permission.ContactsRead, Permission.ContactsCreate],
                ResultingGrantOwnerVersion = 2,
                ResultingAuthorizationEpoch = 2,
            }],
        };

        var interrupted = await fixture.Coordinator.ExecuteAsync(command);

        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, interrupted.Status);
        Assert.Equal(before, await fixture.GrantStore.FindAsync(tenantId, grantId));
        Assert.True(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var row = await context.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Committing, row.State);
            Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
        }

        // Production path: the hosted identity-coordinator recovery drain now invokes ResumeAsync for
        // this same pre-audit fault, so an operator no longer needs an admin retry to clear the fence.
        var retired = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.ResumeAsync(command.CorrelationId));

        Assert.Contains("identity.grant_permission_mutation_retired", retired.Message, StringComparison.Ordinal);
        Assert.Equal(before, await fixture.GrantStore.FindAsync(tenantId, grantId));
    }

    [Fact]
    public async Task Hosted_Recovery_Drain_Runs_At_Startup()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.Tenants[0].Authority = new ThrowBeforeFinalizeOnceStore(fixture.Tenants[0].Authority);

        var pending = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, pending.Status);

        using var host = BuildRecoveryHost(fixture, TimeSpan.FromHours(1));
        await host.StartAsync();
        await WaitForCoordinatorStateAsync(fixture, InstallationIdentityCoordinatorState.Completed);

        Assert.False(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        await host.StopAsync();
    }

    [Fact]
    public async Task Hosted_Recovery_Drain_Runs_Periodically_After_Startup()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.Tenants[0].Authority = new ThrowBeforeFinalizeOnceStore(fixture.Tenants[0].Authority);
        var interval = TimeSpan.FromMinutes(1);
        var clock = new ManualPeriodicTimeProvider(FixedNow);

        using var host = BuildRecoveryHost(fixture, interval, clock);
        await host.StartAsync();
        await host.Services
            .GetRequiredService<InstallationIdentityCoordinatorRecoveryDaemon>()
            .StartupDrainCompleted;

        var pending = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, pending.Status);
        clock.Advance(interval);
        await WaitForCoordinatorStateAsync(fixture, InstallationIdentityCoordinatorState.Completed);

        Assert.False(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        await host.StopAsync();
    }

    [Fact]
    public async Task Hosted_Recovery_Drain_Does_Not_Stop_Host_When_Initial_Scan_Fails()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(0);
        var recovery = new InstallationIdentityCoordinatorRecoveryService(
            new ThrowingIdentityContextFactory(),
            fixture.Coordinator);

        using var host = BuildRecoveryHost(recovery, TimeSpan.FromMilliseconds(10));
        await host.StartAsync();
        await Task.Delay(250);

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
    }

    [Fact]
    public async Task Hosted_Recovery_Drain_Skips_A_Poisoned_Row_And_Heals_Later_Rows()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        var account = new CoordinatorFixture.ActorVersion(
            fixture.AccountId,
            fixture.AccountOwnerVersion,
            fixture.AccountSecurityVersion);

        fixture.Tenants[0].Authority = new ThrowBeforeFinalizeOnceStore(fixture.Tenants[0].Authority);
        var poisonedCommand = fixture.CommandForAccount(
            account,
            [fixture.Tenants[0]],
            "a-poisoned-row",
            "poisoned",
            "poisoned");
        Assert.Equal(
            InstallationIdentityCoordinationStatus.PendingRecovery,
            (await fixture.Coordinator.ExecuteAsync(poisonedCommand)).Status);
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var row = await context.Coordinators.SingleAsync(
                item => item.CorrelationId == poisonedCommand.CorrelationId);
            row.IntentPayloadJson = "{poisoned";
            await context.SaveChangesAsync();
        }

        fixture.Tenants[1].Authority = new ThrowBeforeFinalizeOnceStore(fixture.Tenants[1].Authority);
        var healableCommand = fixture.CommandForAccount(
            account,
            [fixture.Tenants[1]],
            "b-healable-row",
            "healable",
            "healable");
        Assert.Equal(
            InstallationIdentityCoordinationStatus.PendingRecovery,
            (await fixture.Coordinator.ExecuteAsync(healableCommand)).Status);

        using var host = BuildRecoveryHost(fixture, TimeSpan.FromHours(1));
        await host.StartAsync();
        await WaitForCoordinatorStateAsync(
            fixture,
            InstallationIdentityCoordinatorState.Completed,
            healableCommand.CorrelationId);

        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await context.Coordinators.SingleAsync(
                    item => item.CorrelationId == poisonedCommand.CorrelationId)).State);
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task Postcommit_Tenant_Unavailability_Remains_Fenced_And_Cannot_Abort()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.Tenants[0].Authority = new AlwaysFailAfterFinalizeStore(fixture.Tenants[0].Authority);

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        var resumed = await fixture.Coordinator.ResumeAsync("membership-command");

        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, result.Status);
        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, resumed.Status);
        Assert.True(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        await using var context = fixture.HomeFactory.CreateDbContext();
        var row = await context.Coordinators.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCoordinatorState.Committing, row.State);
        Assert.NotEqual(InstallationIdentityCoordinatorState.Aborted, row.State);
    }

    [Fact]
    public async Task Stale_Installation_Account_Version_Is_Refused_Before_A_Coordinator_Row_Exists()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var command = fixture.Command(fixture.Tenants) with
        {
            ExpectedAccountSecurityVersion = 99,
            ExpectedActorSecurityVersion = 99,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteAsync(command));

        Assert.StartsWith("identity.account_version_stale:", exception.Message);
        await using var context = fixture.HomeFactory.CreateDbContext();
        Assert.Empty(await context.Coordinators.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Account_Change_During_Prepare_Aborts_Before_The_Commit_Decision()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.SetAdmission(new MutateAccountOnceAdmission(fixture.HomeFactory, fixture.AccountId));

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.Null(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
        Assert.False(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
    }

    [Fact]
    public async Task Distinct_Actor_Change_During_Prepare_Aborts_Before_The_Commit_Decision()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var actor = await fixture.CreateActiveActorAsync();
        fixture.SetAdmission(new MutateAccountOnceAdmission(fixture.HomeFactory, actor.AccountId));

        var result = await fixture.Coordinator.ExecuteAsync(
            fixture.Command(fixture.Tenants, actor));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.Null(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task Canonical_Tenant_Authority_Refusal_Aborts_Without_Membership()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.SetAdmission(new RejectingAdmission());

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.Null(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_Tenant_Lease_Aborts_Before_The_Commit_Decision()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        fixture.Tenants[0].Leases = new ExpiredLeaseCoordinator();

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.Null(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task Completed_Replay_Refuses_Tampered_Home_Receipts()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var command = fixture.Command(fixture.Tenants);
        Assert.Equal(
            InstallationIdentityCoordinationStatus.Completed,
            (await fixture.Coordinator.ExecuteAsync(command)).Status);
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var row = await context.Coordinators.SingleAsync();
            row.FinalReceiptsJson = row.FinalReceiptsJson.Replace(
                "membershipDigest\":\"", "membershipDigest\":\"TAMPERED", StringComparison.Ordinal);
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteAsync(command));
        Assert.StartsWith("identity.coordinator_evidence_invalid:", exception.Message);
    }

    [Fact]
    public async Task Audit_Append_Failure_After_Tenant_Finalization_Resumes_Forward()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER refuse_coordination_audit
                BEFORE INSERT ON installation_audit_envelopes
                WHEN NEW.sequence > 1
                BEGIN
                    SELECT RAISE(ABORT, 'coordination audit refused');
                END;
                """);
        }

        var pending = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, pending.Status);
        Assert.True(await fixture.Coordinator.IsAccountFencedAsync(fixture.AccountId));
        Assert.NotNull(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Finalizing,
                (await context.Coordinators.AsNoTracking().SingleAsync()).State);
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_coordination_audit;");
        }

        Assert.Equal(
            InstallationIdentityCoordinationStatus.Completed,
            (await fixture.Coordinator.ResumeAsync("membership-command")).Status);
    }

    [Fact]
    public async Task Corrupt_Installation_Audit_Aborts_Before_Tenant_Commit()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        await using (var context = fixture.HomeFactory.CreateDbContext())
        {
            var head = await context.AuditHeads.SingleAsync();
            head.HeadHash = new string('F', 64);
            await context.SaveChangesAsync();
        }

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.Aborted, result.Status);
        Assert.Null(await fixture.Tenants[0].Authority.GetMembershipAsync(
            fixture.AccountId, CancellationToken.None));
    }

    private static IHost BuildRecoveryHost(
        CoordinatorFixture fixture,
        TimeSpan interval,
        TimeProvider? time = null) =>
        BuildRecoveryHost(
            new InstallationIdentityCoordinatorRecoveryService(fixture.HomeFactory, fixture.Coordinator),
            interval,
            time);

    private static IHost BuildRecoveryHost(
        InstallationIdentityCoordinatorRecoveryService recovery,
        TimeSpan interval,
        TimeProvider? time = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddSingleton(recovery);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton<InstallationIdentityCoordinatorRecoveryDaemon>(sp =>
            new InstallationIdentityCoordinatorRecoveryDaemon(
                sp.GetRequiredService<InstallationIdentityCoordinatorRecoveryService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<InstallationIdentityCoordinatorRecoveryDaemon>>(),
                interval));
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<InstallationIdentityCoordinatorRecoveryDaemon>());
        return builder.Build();
    }

    private static async Task WaitForCoordinatorStateAsync(
        CoordinatorFixture fixture,
        InstallationIdentityCoordinatorState expected,
        string? correlationId = null)
    {
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(10).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await using var context = fixture.HomeFactory.CreateDbContext();
            var query = context.Coordinators.AsNoTracking();
            var row = correlationId is null
                ? await query.SingleAsync()
                : await query.SingleAsync(item => item.CorrelationId == correlationId);
            if (row.State == expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        await using var finalContext = fixture.HomeFactory.CreateDbContext();
        var finalRow = correlationId is null
            ? await finalContext.Coordinators.AsNoTracking().SingleAsync()
            : await finalContext.Coordinators.AsNoTracking()
                .SingleAsync(item => item.CorrelationId == correlationId);
        Assert.Equal(expected, finalRow.State);
    }

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        private readonly string _homePath;

        private CoordinatorFixture(
            string homePath,
            InstallationFounderBootstrapServiceTests.IdentityContextFactory homeFactory,
            string accountId,
            long accountOwnerVersion,
            long accountSecurityVersion,
            List<TenantPartitionFixture> tenants,
            InMemoryGrantStore grantStore)
        {
            _homePath = homePath;
            HomeFactory = homeFactory;
            AccountId = accountId;
            AccountOwnerVersion = accountOwnerVersion;
            AccountSecurityVersion = accountSecurityVersion;
            Tenants = tenants;
            GrantStore = grantStore;
            _resolver = new FixturePartitionResolver(tenants);
            SetAdmission(new AcceptingAdmission());
        }

        public InstallationFounderBootstrapServiceTests.IdentityContextFactory HomeFactory { get; }
        public string AccountId { get; }
        public long AccountOwnerVersion { get; }
        public long AccountSecurityVersion { get; }
        public List<TenantPartitionFixture> Tenants { get; }
        public InMemoryGrantStore GrantStore { get; }
        private readonly FixturePartitionResolver _resolver;
        public InstallationIdentityCoordinatorService Coordinator { get; private set; } = null!;

        public void SetAdmission(ITenantMembershipAuthorityAdmission admission) =>
            Coordinator = new InstallationIdentityCoordinatorService(
                HomeFactory,
                _resolver,
                admission,
                new FixedTimeProvider(FixedNow),
                TestAuthorization.Gate(true),
                GrantStore);

        public static async Task<CoordinatorFixture> CreateAsync(int tenantCount)
        {
            var homePath = Path.Combine(
                Path.GetTempPath(), $"harborline-identity-coordinator-{Guid.NewGuid():N}.db");
            var factory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(homePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            var bootstrap = new InstallationFounderBootstrapService(
                factory,
                new FixedTimeProvider(FixedNow));
            var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                "$argon2id$v=19$m=19456,t=2,p=1$" +
                Convert.ToBase64String(new byte[16]) + "$" +
                Convert.ToBase64String(new byte[32]),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap"));

            string accountId;
            long ownerVersion;
            long securityVersion;
            await using (var context = factory.CreateDbContext())
            {
                var account = await context.Accounts.AsNoTracking().SingleAsync();
                accountId = account.AccountId;
                ownerVersion = account.OwnerVersion;
                securityVersion = account.SecurityVersion;
            }
            Assert.Equal(founder.AccountId, accountId);

            var tenants = new List<TenantPartitionFixture>();
            for (var index = 0; index < tenantCount; index++)
            {
                tenants.Add(await TenantPartitionFixture.CreateAsync(factory));
            }
            return new CoordinatorFixture(
                homePath,
                factory,
                accountId,
                ownerVersion,
                securityVersion,
                tenants,
                TestInMemoryAuthorizationStores.GrantStore());
        }

        public InstallationIdentityCoordinationCommand Command(
            IReadOnlyList<TenantPartitionFixture> tenants,
            ActorVersion? actor = null)
        {
            var selectedActor = actor ?? new ActorVersion(
                AccountId,
                AccountOwnerVersion,
                AccountSecurityVersion);
            return CommandForAccount(
                new ActorVersion(AccountId, AccountOwnerVersion, AccountSecurityVersion),
                tenants,
                "membership-command",
                "principal",
                "grant",
                selectedActor);
        }

        public InstallationIdentityCoordinationCommand CommandForAccount(
            ActorVersion account,
            IReadOnlyList<TenantPartitionFixture> tenants,
            string correlationId,
            string principalPrefix,
            string grantPrefix,
            ActorVersion? actor = null)
        {
            var selectedActor = actor ?? account;
            return new(
                correlationId,
                account.AccountId,
                selectedActor.AccountId,
                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                account.OwnerVersion,
                account.SecurityVersion,
                selectedActor.OwnerVersion,
                selectedActor.SecurityVersion,
                tenants.Select((tenant, index) => new TenantMembershipMutation(
                    tenant.TenantId,
                    $"{principalPrefix}-{index}",
                    $"{grantPrefix}-{index}",
                    ExpectedGrantOwnerVersion: 1,
                    AuthorizationEpoch: 1,
                    ExpectedMembershipOwnerVersion: 0,
                    TenantMembershipStatus.Active)).ToArray());
        }

        public async Task<ActorVersion> CreateActiveActorAsync()
        {
            var now = FixedNow;
            var actor = new InstallationAccountRecord
            {
                AccountId = Guid.NewGuid().ToString("N"),
                NormalizedUsername = "ACTOR-" + Guid.NewGuid().ToString("N"),
                CredentialHash = "$argon2id$test",
                CredentialAlgorithm = "argon2id",
                CredentialCeremonyId = Guid.NewGuid().ToString("N"),
                CredentialVersion = 1,
                Status = InstallationAccountStatus.Active,
                SecurityVersion = 1,
                OwnerVersion = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await using var context = HomeFactory.CreateDbContext();
            context.Accounts.Add(actor);
            await context.SaveChangesAsync();
            return new ActorVersion(actor.AccountId, actor.OwnerVersion, actor.SecurityVersion);
        }

        public sealed record ActorVersion(string AccountId, long OwnerVersion, long SecurityVersion);

        public async ValueTask DisposeAsync()
        {
            foreach (var tenant in Tenants)
            {
                await tenant.DisposeAsync();
            }
            if (File.Exists(_homePath))
            {
                File.Delete(_homePath);
            }
        }
    }

    private sealed class ThrowingIdentityContextFactory
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext() =>
            throw new InvalidOperationException("test identity store scan failure");
    }

    private sealed class TenantPartitionFixture : IAsyncDisposable
    {
        private readonly string _path;
        private readonly SqlCipherEncryptedStore _store;

        private TenantPartitionFixture(
            string path,
            SqlCipherEncryptedStore store,
            string tenantId,
            ITenantMembershipAuthorityStore authority)
        {
            _path = path;
            _store = store;
            TenantId = tenantId;
            Authority = authority;
            Leases = new AlwaysLeaseCoordinator();
        }

        public string TenantId { get; }
        public ITenantMembershipAuthorityStore Authority { get; set; }
        public ILeaseCoordinator Leases { get; set; }

        public static async Task<TenantPartitionFixture> CreateAsync(
            InstallationFounderBootstrapServiceTests.IdentityContextFactory homeFactory)
        {
            var path = Path.Combine(Path.GetTempPath(), $"harborline-tenant-partition-{Guid.NewGuid():N}.db");
            var store = new SqlCipherEncryptedStore();
            await store.OpenAsync(path, RandomNumberGenerator.GetBytes(32), CancellationToken.None);
            var tenantId = Guid.NewGuid().ToString("D");
            return new TenantPartitionFixture(
                path,
                store,
                tenantId,
                new EncryptedTenantMembershipAuthorityStore(
                    store,
                    tenantId,
                    new InstallationIdentityHomeDecisionAuthority(homeFactory),
                    new FixedTimeProvider(FixedNow)));
        }

        public async ValueTask DisposeAsync()
        {
            await Leases.DisposeAsync();
            await _store.DisposeAsync();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    private sealed class FixturePartitionResolver(IReadOnlyList<TenantPartitionFixture> tenants)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixture = tenants.Single(item => item.TenantId == tenantId);
            return Task.FromResult(new TenantIdentityAuthorityPartition(
                fixture.TenantId, fixture.Authority, fixture.Leases));
        }
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var now = FixedNow;
            var lease = new Lease(
                Guid.NewGuid().ToString("N"), resourceId, "test-node", now, now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) => _held.Values.Any(item => item.ResourceId == resourceId);
        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExpiredLeaseCoordinator : ILeaseCoordinator
    {
        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct) =>
            Task.FromResult<Lease?>(new Lease(
                Guid.NewGuid().ToString("N"),
                resourceId,
                "expired-node",
                FixedNow - duration,
                FixedNow - TimeSpan.FromSeconds(1),
                []));
        public Task ReleaseAsync(Lease lease, CancellationToken ct) => Task.CompletedTask;
        public bool Holds(string resourceId) => false;
        public IReadOnlyCollection<Lease> HeldLeases => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class RejectingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("canonical tenant authority refused the mutation");

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class RejectExistingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("canonical tenant authority is stale");
    }

    private sealed class MutateAccountOnceAdmission(
        InstallationFounderBootstrapServiceTests.IdentityContextFactory factory,
        string accountId) : ITenantMembershipAuthorityAdmission
    {
        private int _remaining = 1;

        public async Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string observedAccountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken)
        {
            Assert.Equal(accountId, observedAccountId);
            if (Interlocked.Exchange(ref _remaining, 0) != 1)
            {
                return;
            }
            await using var context = factory.CreateDbContext();
            var account = await context.Accounts.SingleAsync(
                item => item.AccountId == accountId,
                cancellationToken);
            account.SecurityVersion++;
            account.OwnerVersion++;
            await context.SaveChangesAsync(cancellationToken);
        }

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private abstract class DelegatingMembershipStore(ITenantMembershipAuthorityStore inner)
        : ITenantMembershipAuthorityStore
    {
        protected ITenantMembershipAuthorityStore Inner { get; } = inner;
        public string TenantId => Inner.TenantId;
        public virtual Task PrepareAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string actorAccountId,
            string authorityEvidenceDigest,
            TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.PrepareAsync(
                correlationId,
                commandFingerprint,
                accountId,
                actorAccountId,
                authorityEvidenceDigest,
                mutation,
                occurredAtUtc,
                cancellationToken);
        public virtual Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.FinalizeAsync(correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
        public Task AbortAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.AbortAsync(correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId,
            CancellationToken cancellationToken) => Inner.GetMembershipAsync(accountId, cancellationToken);
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId,
            CancellationToken cancellationToken) => Inner.GetIntentStateAsync(correlationId, cancellationToken);
        public Task<bool> IsAdmissionBlockedAsync(
            string accountId,
            CancellationToken cancellationToken) => Inner.IsAdmissionBlockedAsync(accountId, cancellationToken);
        public Task PrepareSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string membershipId,
            string payloadDigest,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.PrepareSessionSelectionAsync(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                payloadDigest,
                occurredAtUtc,
                cancellationToken);
        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.FinalizeSessionSelectionAsync(
                correlationId,
                commandFingerprint,
                occurredAtUtc,
                cancellationToken);
        public Task AbortSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.AbortSessionSelectionAsync(
                correlationId,
                commandFingerprint,
                occurredAtUtc,
                cancellationToken);
        public Task PrepareSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string membershipId,
            string sessionCorrelationId,
            string payloadDigest,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.PrepareSessionRevocationAsync(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                sessionCorrelationId,
                payloadDigest,
                occurredAtUtc,
                cancellationToken);
        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.FinalizeSessionRevocationAsync(
                correlationId,
                commandFingerprint,
                occurredAtUtc,
                cancellationToken);
        public Task AbortSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Inner.AbortSessionRevocationAsync(
                correlationId,
                commandFingerprint,
                occurredAtUtc,
                cancellationToken);
    }

    private sealed class FailPrepareStore(ITenantMembershipAuthorityStore inner)
        : DelegatingMembershipStore(inner)
    {
        public override Task PrepareAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string actorAccountId,
            string authorityEvidenceDigest,
            TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("injected prepare failure");
    }

    private sealed class ThrowAfterFinalizeOnceStore(ITenantMembershipAuthorityStore inner)
        : DelegatingMembershipStore(inner)
    {
        private int _remaining = 1;

        public override async Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            var receipt = await Inner.FinalizeAsync(
                correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
            if (Interlocked.Exchange(ref _remaining, 0) == 1)
            {
                throw new InvalidOperationException("injected response loss after tenant commit");
            }
            return receipt;
        }
    }

    private sealed class ThrowBeforeFinalizeOnceStore(ITenantMembershipAuthorityStore inner)
        : DelegatingMembershipStore(inner)
    {
        private int _remaining = 1;

        public override Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _remaining, 0) == 1)
            {
                throw new InvalidOperationException("injected fault at the previous grant window");
            }

            return base.FinalizeAsync(
                correlationId,
                commandFingerprint,
                occurredAtUtc,
                cancellationToken);
        }
    }

    private sealed class AlwaysFailAfterFinalizeStore(ITenantMembershipAuthorityStore inner)
        : DelegatingMembershipStore(inner)
    {
        public override async Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            await Inner.FinalizeAsync(correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
            throw new InvalidOperationException("injected persistent response loss after tenant commit");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ManualPeriodicTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                utcNow = utcNow.Add(duration);
                foreach (var timer in _timers)
                {
                    timer.CollectDueCallbacks(utcNow, callbacks);
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualPeriodicTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset? _next;
            private TimeSpan _period;
            private bool _disposed;

            internal ManualTimer(
                ManualPeriodicTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                ChangeCore(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    ChangeCore(dueTime, period);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    _disposed = true;
                    _next = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void CollectDueCallbacks(
                DateTimeOffset now,
                List<(TimerCallback Callback, object? State)> callbacks)
            {
                while (!_disposed && _next is { } next && next <= now)
                {
                    callbacks.Add((_callback, _state));
                    _next = _period == Timeout.InfiniteTimeSpan ? null : next.Add(_period);
                }
            }

            private void ChangeCore(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _next = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow().Add(dueTime);
            }
        }
    }
}
