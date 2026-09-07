using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Tests.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Production-bound proofs for the four ADR 0160 R3-H legacy-v1 cutover authorities: the exclusive
/// restart-safe migration lease, the durable v1 mutation write-barrier, the atomic v2
/// authority-marker CAS, and post-CAS rejection of every legacy bearer audience. Every proof drives
/// the real <see cref="InstallationIdentityCutoverOrchestrator"/> over a file-backed SQLite store and
/// crosses a genuine close/reopen boundary, so a claim of "built" cannot survive the authority being
/// removed, weakened, or reduced to in-memory state.
/// </summary>
/// <remarks>
/// The MTW-00A category-6 legacy-v1 migration sources these authorities must dispose of are
/// NodeWebSessionAuthority (founder credential and web sessions), ActiveTeamAuthorizationContext.LocalUserId (the static
/// local actor), NodeTeamRoster (the install-global live roster), and NodeTenant (the active-team to
/// tenant resolver). The orchestrator is the single seam each of those writers consults, which is why
/// the proofs below assert on the gate rather than on any individual writer.
///
/// Each proof asserts the ADMITTING state BEFORE it asserts the refusal. A gate that refuses
/// unconditionally would satisfy every "must refuse" assertion while making the product unusable, so
/// the positive half is what stops these from passing over a broken authority.
/// </remarks>
internal static class LegacyV1CutoverAuthorityProof
{
    private const string MigrationRunId = "mtw-00c-cutover-run";
    private const string WatermarkDigest = "mtw-00c-watermark-digest";
    private const string VerificationDigest = "mtw-00c-verification-digest";

    /// <summary>Matches the "100-way race" the fixture's declared reason promises.</summary>
    private const int ConcurrentCasAttempts = 100;

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 4, 0, 0, TimeSpan.Zero);

    private static InstallationIdentityLegacyBearerAudience[] AllAudiences() =>
        Enum.GetValues<InstallationIdentityLegacyBearerAudience>();

    internal static void ProveMigrationLeaseIsExclusiveAndRestartSafe() =>
        Run(static async store =>
        {
            var first = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            var acquired = await first.AcquireLeaseAsync("coordinator-A", TimeSpan.FromHours(1));
            Require(
                acquired.Status == InstallationIdentityLeaseAcquireStatus.Acquired,
                "The first holder could not acquire the installation migration lease.");
            Require(acquired.Lease is not null, "An acquired lease carried no capability.");

            var contested = await first.AcquireLeaseAsync("coordinator-B", TimeSpan.FromHours(1));
            Require(
                contested.Status == InstallationIdentityLeaseAcquireStatus.HeldByAnother,
                "A second holder acquired the migration lease while the first lease was live.");
            Require(contested.Lease is null, "A refused acquisition still handed back a capability.");

            store.Restart();

            var afterRestart = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            var stillHeld = await afterRestart.AcquireLeaseAsync("coordinator-B", TimeSpan.FromHours(1));
            Require(
                stillHeld.Status == InstallationIdentityLeaseAcquireStatus.HeldByAnother,
                "The migration lease did not survive a store close/reopen: a second holder acquired " +
                "it after the restart.");

            // The first holder's capability is still honoured across the same restart — exclusivity
            // that also locked out the rightful holder would be a brick, not a lease.
            var advanced = await afterRestart.AdvanceAsync(
                acquired.Lease!,
                InstallationIdentityCutoverStage.WriteBarrierActive,
                Evidence());
            Require(
                advanced.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
                "The surviving lease holder could not advance the cutover after the restart.");

            var forged = await afterRestart.AdvanceAsync(
                acquired.Lease! with { LeaseId = "forged-lease-id" },
                InstallationIdentityCutoverStage.Copying,
                Evidence());
            Require(
                forged.Status == InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld,
                "An altered lease capability was accepted; the lease is not revalidated in-store.");
        });

    internal static void ProveV1MutationWriteBarrierBlocksMutationWhileLeaseHeld() =>
        Run(static async store =>
        {
            var orchestrator = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());

            var beforeBarrier = await orchestrator.CheckV1MutationAdmissionAsync();
            Require(
                beforeBarrier.IsAllowed,
                "The v1 mutation gate refused before any barrier was raised — a gate that always " +
                "refuses proves nothing about the barrier.");
            foreach (var audience in AllAudiences())
            {
                var admitted = await orchestrator.CheckLegacyBearerAdmissionAsync(audience);
                Require(
                    admitted.IsAllowed,
                    $"The legacy bearer gate refused audience '{audience}' before the barrier rose.");
            }

            var lease = (await orchestrator.AcquireLeaseAsync("coordinator-A", TimeSpan.FromHours(1))).Lease;
            Require(lease is not null, "The migration lease could not be acquired.");
            var raised = await orchestrator.AdvanceAsync(
                lease!,
                InstallationIdentityCutoverStage.WriteBarrierActive,
                Evidence());
            Require(
                raised.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
                "The v1 write barrier could not be raised.");

            store.Restart();

            var restarted = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            var mutation = await restarted.CheckV1MutationAdmissionAsync();
            Require(
                !mutation.IsAllowed,
                "A v1 mutation was admitted while the write barrier was durably raised.");
            Require(
                string.Equals(
                    mutation.RefusalCode,
                    InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal,
                    StringComparison.Ordinal),
                $"The barrier refused with '{mutation.RefusalCode}' instead of the canonical " +
                $"'{InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal}'.");

            foreach (var audience in AllAudiences())
            {
                var bearer = await restarted.CheckLegacyBearerAdmissionAsync(audience);
                Require(
                    !bearer.IsAllowed,
                    $"Legacy bearer audience '{audience}' was admitted behind a raised barrier.");
                Require(
                    string.Equals(
                        bearer.RefusalCode,
                        InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal,
                        StringComparison.Ordinal),
                    $"Audience '{audience}' refused with '{bearer.RefusalCode}' instead of the " +
                    "canonical migration-in-progress code.");
            }
        });

    internal static void ProveV2AuthorityMarkerCasFlipsExactlyOnce() =>
        Run(static async store =>
        {
            var orchestrator = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            var lease = (await orchestrator.AcquireLeaseAsync("coordinator-A", TimeSpan.FromHours(1))).Lease;
            Require(lease is not null, "The migration lease could not be acquired.");
            await AdvanceThroughVerifiedAsync(orchestrator, lease!);

            var premature = await orchestrator.AdvanceAsync(
                lease!,
                InstallationIdentityCutoverStage.V2Authoritative,
                Evidence());
            Require(
                premature.Status == InstallationIdentityCutoverAdvanceStatus.InvariantRefused &&
                    string.Equals(
                        premature.RefusalCode,
                        "legacy_bearer_revocation_not_staged",
                        StringComparison.Ordinal),
                "The marker CAS was allowed before the legacy bearer revocation evidence was durable.");

            var partial = await orchestrator.StageLegacyBearerRevocationsAsync(
                lease!,
                [InstallationIdentityLegacyBearerAudience.Tooling]);
            Require(
                partial.Status == InstallationIdentityLegacyBearerRevocationStatus.InvariantRefused,
                "An incomplete legacy bearer audience set was accepted as revocation evidence.");

            var staged = await orchestrator.StageLegacyBearerRevocationsAsync(lease!, AllAudiences());
            Require(
                staged.Status == InstallationIdentityLegacyBearerRevocationStatus.Staged,
                "The complete legacy bearer audience set was not staged.");

            var attempts = await Task.WhenAll(
                Enumerable.Range(0, ConcurrentCasAttempts).Select(_ =>
                    new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry())
                        .AdvanceAsync(
                            lease!,
                            InstallationIdentityCutoverStage.V2Authoritative,
                            Evidence())));

            var winners = attempts.Count(result =>
                result.Status == InstallationIdentityCutoverAdvanceStatus.Advanced);
            Require(
                winners == 1,
                $"The v2 authority-marker CAS produced {winners} winners under a " +
                $"{ConcurrentCasAttempts}-way race; exactly one is required.");
            Require(
                attempts.All(result => result.Status
                    is InstallationIdentityCutoverAdvanceStatus.Advanced
                    or InstallationIdentityCutoverAdvanceStatus.AlreadyAtStage
                    or InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld),
                "A losing CAS attempt returned an outcome outside the permitted refusal set.");

            store.Restart();

            var restarted = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            var authority = await restarted.ResolveAuthorityAsync();
            Require(
                authority.IsReadable,
                $"The committed v2 marker was unreadable after restart ('{authority.RefusalCode}').");
            Require(
                authority.Stage == InstallationIdentityCutoverStage.V2Authoritative,
                "The committed cutover stage did not survive the restart.");
            Require(
                authority.Authority == InstallationIdentityAuthorityKind.Revision3V2,
                "The resolved authority after the CAS was not the revision-3 v2 authority.");
        });

    internal static void ProvePostCutoverV1BearerIsRejected()
    {
        ProveOrchestratorRefusesEveryAudienceAfterTheMarker();
        ProveProductionAcceptPathsRefuseAfterTheMarker();
    }

    /// <summary>
    /// The PRODUCTION accept paths, driven end to end across a real committed marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The orchestrator half of this proof is audience-INSENSITIVE: <c>CheckLegacyBearerAdmissionAsync</c>
    /// reads one durable stage and ignores which audience it was handed, so looping over the four enum
    /// values re-runs a single stage check four times. It can prove the gate refuses; it cannot prove
    /// that any production path ASKS the gate. An accept path that never consults the seam stays fully
    /// reachable after the marker commits while the loop above stays green — which is exactly how
    /// <see cref="WebTenantSelectionAuthority"/> and the account-challenge issuer went unwired.
    /// </para>
    /// <para>
    /// Each delegate below drives a real authority over its own file-backed store, asserts the
    /// ADMITTING state first, then commits a real v2 marker through
    /// <see cref="CutoverAdvance"/> and asserts the same call is refused. Unwire any one of them from
    /// <see cref="IInstallationIdentityV1AuthorityGate"/> and this proof goes red.
    /// </para>
    /// </remarks>
    private static void ProveProductionAcceptPathsRefuseAfterTheMarker()
    {
        // The ISSUE half of the AccountChallenge audience.
        new WebAccountAccessChallengeIssuerTests()
            .Challenge_Issue_Is_Refused_After_The_Real_V2_Marker_Commits()
            .GetAwaiter()
            .GetResult();

        // The CONSUME half: a challenge already in a browser's hands when the marker commits.
        new WebTenantSelectionAuthorityTests()
            .Select_Is_Refused_After_The_Real_V2_Marker_Commits()
            .GetAwaiter()
            .GetResult();

        // The legacy web-session audiences (SelectedUser / Tooling) plus the v1 login mutation.
        new NodeWebSessionAuthorityTests()
            .Committed_V2_Marker_Rejects_Legacy_Cookie_Tooling_And_New_Login()
            .GetAwaiter()
            .GetResult();

        // Revocation is deliberately NOT gated, and that asymmetry needs a guard of its own: a
        // future "consult the gate everywhere" sweep that re-gated logout would restore the #3245
        // defect, leaving live v1 records behind a 204.
        new NodeWebSessionAuthorityTests()
            .PostCutoverLogout_PresentingBothAudiences_RevokesTheLegacyRecord()
            .GetAwaiter()
            .GetResult();
    }

    private static void ProveOrchestratorRefusesEveryAudienceAfterTheMarker() =>
        Run(static async store =>
        {
            var orchestrator = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            foreach (var audience in AllAudiences())
            {
                var admitted = await orchestrator.CheckLegacyBearerAdmissionAsync(audience);
                Require(
                    admitted.IsAllowed,
                    $"Legacy bearer audience '{audience}' was refused before the cutover — the " +
                    "post-cutover rejection below would then prove nothing.");
            }

            var lease = (await orchestrator.AcquireLeaseAsync("coordinator-A", TimeSpan.FromHours(1))).Lease;
            Require(lease is not null, "The migration lease could not be acquired.");
            await AdvanceThroughVerifiedAsync(orchestrator, lease!);
            var staged = await orchestrator.StageLegacyBearerRevocationsAsync(lease!, AllAudiences());
            Require(
                staged.Status is InstallationIdentityLegacyBearerRevocationStatus.Staged
                    or InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged,
                "The complete legacy bearer audience set was not staged.");
            var committed = await orchestrator.AdvanceAsync(
                lease!,
                InstallationIdentityCutoverStage.V2Authoritative,
                Evidence());
            Require(
                committed.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
                "The v2 authority marker did not commit.");

            store.Restart();

            var restarted = new InstallationIdentityCutoverOrchestrator(store.Factory, Clock, SuccessorRegistry());
            foreach (var audience in AllAudiences())
            {
                var refused = await restarted.CheckLegacyBearerAdmissionAsync(audience);
                Require(
                    !refused.IsAllowed,
                    $"Legacy bearer audience '{audience}' survived the authority-version flip.");
                Require(
                    string.Equals(
                        refused.RefusalCode,
                        InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal,
                        StringComparison.Ordinal),
                    $"Audience '{audience}' refused with '{refused.RefusalCode}' instead of the " +
                    "permanent retirement code; a migration-in-progress answer implies the v1 " +
                    "authority could still come back.");
            }

            var mutation = await restarted.CheckV1MutationAdmissionAsync();
            Require(!mutation.IsAllowed, "A v1 mutation was admitted after the marker committed to v2.");

            // No downgrade path: whatever a caller attempts, the marker stays v2.
            await restarted.AdvanceAsync(
                lease!,
                InstallationIdentityCutoverStage.LegacyV1Authoritative,
                Evidence());
            var authority = await restarted.ResolveAuthorityAsync();
            Require(
                authority.Authority == InstallationIdentityAuthorityKind.Revision3V2 &&
                    authority.Stage == InstallationIdentityCutoverStage.V2Authoritative,
                "The committed v2 authority marker was walked back to the legacy v1 authority.");
        });

    private static async Task AdvanceThroughVerifiedAsync(
        InstallationIdentityCutoverOrchestrator orchestrator,
        InstallationIdentityMigrationLease lease)
    {
        foreach (var stage in new[]
        {
            InstallationIdentityCutoverStage.WriteBarrierActive,
            InstallationIdentityCutoverStage.Copying,
            InstallationIdentityCutoverStage.Verified,
        })
        {
            var result = await orchestrator.AdvanceAsync(lease, stage, Evidence());
            Require(
                result.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
                $"The cutover could not advance to '{stage}' ('{result.RefusalCode}').");
        }
    }

    private static InstallationIdentityCutoverEvidence Evidence() =>
        new(MigrationRunId, WatermarkDigest, VerificationDigest);

    private static TimeProvider Clock => new FixedTimeProvider(Now);

    private static void Run(Func<CutoverProofStore, Task> proof)
    {
        RunAsync(proof).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Func<CutoverProofStore, Task> proof)
    {
        await using var store = await CutoverProofStore.CreateAsync().ConfigureAwait(false);
        await proof(store).ConfigureAwait(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// A prepared, file-backed installation-identity store: real EF migrations, a real founder
    /// bootstrap (which mints the account, grant, and root designation the v2 candidate check reads),
    /// and the migration source watermark the verification stage requires.
    /// </summary>
    private sealed class CutoverProofStore : IAsyncDisposable
    {
        private readonly string _databasePath;

        private CutoverProofStore(string databasePath, IdentityContextFactory factory)
        {
            _databasePath = databasePath;
            Factory = factory;
        }

        public IdentityContextFactory Factory { get; private set; }

        public static async Task<CutoverProofStore> CreateAsync()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"mtw00c-cutover-{Guid.NewGuid():N}.sqlite");
            var factory = new IdentityContextFactory(databasePath);
            try
            {
                await using (var context = factory.CreateDbContext())
                {
                    await context.Database.MigrateAsync().ConfigureAwait(false);
                }

                var bootstrap = new InstallationFounderBootstrapService(
                    factory,
                    new FixedTimeProvider(Now));
                var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                        "founder",
                        "$argon2id$v=19$m=19456,t=2,p=1$" +
                            Convert.ToBase64String(new byte[16]) + "$" +
                            Convert.ToBase64String(new byte[32]),
                        Guid.NewGuid().ToString("N"),
                        string.Join(":", Enumerable.Repeat("AB", 32)),
                        "mtw-00c-cutover-proof"))
                    .ConfigureAwait(false);
                Require(
                    founder.Status == InstallationFounderBootstrapStatus.Created,
                    $"The proof store could not bootstrap a founder ('{founder.Status}').");

                await using (var context = factory.CreateDbContext())
                {
                    context.MigrationSourceWatermarks.Add(
                        new InstallationIdentityMigrationSourceWatermarkRecord
                        {
                            SourceKind = "legacy-installation-account",
                            SourcePartition = "singleton",
                            MigrationRunId = MigrationRunId,
                            SourceVersion = 7,
                            HighWatermark = "7",
                            SnapshotDigest = WatermarkDigest,
                            OwnerVersion = 1,
                            CapturedAtUtc = Now,
                        });
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }

                return new CutoverProofStore(databasePath, factory);
            }
            catch
            {
                // No store was handed back, so nothing will ever dispose this file. Clean it up here
                // and let the original failure propagate untouched.
                Cleanup(databasePath);
                throw;
            }
        }

        /// <summary>
        /// A genuine process-restart boundary: every pooled connection to the file is closed and a
        /// brand-new context factory is bound to the same path, so anything the proof reads back was
        /// read from disk rather than from a live handle or an EF change tracker.
        /// </summary>
        public void Restart()
        {
            SqliteConnection.ClearAllPools();
            Factory = new IdentityContextFactory(_databasePath);
        }

        public ValueTask DisposeAsync()
        {
            // Best-effort: this runs on the way out of an `await using`, so a failed delete would
            // REPLACE the InvalidOperationException a failing proof is throwing through it and
            // destroy the diagnosis. A leftover temp file is never worth that trade.
            SqliteConnection.ClearAllPools();
            Cleanup(_databasePath);
            return ValueTask.CompletedTask;
        }

        private static void Cleanup(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // Best-effort temp cleanup; a leftover temp file never affects determinism.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup; a leftover temp file never affects determinism.
                }
            }
        }
    }

    private sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// A cutover orchestrator whose successor reports ready. Since #3615 the flip refuses while no
    /// v2 sign-in path is registered, so a fixture that needs to COMMIT the marker has to declare
    /// one. These proofs are about what happens AFTER the flip, so they supply a stub successor
    /// rather than asserting the pre-flip refusal, which is covered separately.
    /// </summary>
    private sealed record ProofSignInPath(string SignInPathName)
        : Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityV2SignInPath;

    private static Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityVersionRegistry
        SuccessorRegistry() =>
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationAuthorityVersionRegistry.CreateDefault(
            [new ProofSignInPath("proof-successor")]);
}
