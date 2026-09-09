using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// ADR 0066 clause 8 — the offline recovery path: environment-derived, node-stopped, loud, and structurally
/// incapable of re-arming the installer.
/// </summary>
[Collection("Harborline process environment")]
public sealed class AdministratorRecoveryCommandTests
{
    [Theory]
    [InlineData(RecoveryRootSelection.ConfigurationOnly)]
    [InlineData(RecoveryRootSelection.CliOnly)]
    [InlineData(RecoveryRootSelection.ConflictingConfigurationAndCli)]
    public async Task Every_root_selector_binds_one_installation_identity_seed_and_database(
        RecoveryRootSelection selection)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var testRoot = NewDirectory();
        var defaultRoot = Path.Combine(testRoot, "default");
        var configuredRoot = Path.Combine(testRoot, "configured");
        var cliRoot = Path.Combine(testRoot, "cli");
        var candidateRoots = new[] { defaultRoot, configuredRoot, cliRoot };
        var effectiveRoot = selection == RecoveryRootSelection.ConfigurationOnly
            ? configuredRoot
            : cliRoot;
        var priorDataDirectory = Environment.GetEnvironmentVariable("LocalNode__DataDirectory");
        var priorRootSeed = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var priorTemp = Environment.GetEnvironmentVariable("TEMP");
        var priorTmp = Environment.GetEnvironmentVariable("TMP");
        try
        {
            foreach (var candidateRoot in candidateRoots)
                Directory.CreateDirectory(candidateRoot);
            await File.WriteAllTextAsync(
                Path.Combine(effectiveRoot, "local-node.db"), "this is not a SQLite database");
            Environment.SetEnvironmentVariable(
                "LocalNode__DataDirectory",
                selection == RecoveryRootSelection.CliOnly ? null : configuredRoot);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", null);
            Environment.SetEnvironmentVariable("TEMP", defaultRoot);
            Environment.SetEnvironmentVariable("TMP", defaultRoot);

            var args = selection == RecoveryRootSelection.ConfigurationOnly
                ? new[] { AdministratorRecoveryCommand.Verb, "--confirm" }
                : new[] { AdministratorRecoveryCommand.Verb, "--confirm", "--data-dir", cliRoot };
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var resolver = new RecordingInstallationResolver(defaultRoot);

            var exit = await AdministratorRecoveryCommand.RunAsync(
                args, stdout, stderr, TimeProvider.System, resolver);

            Assert.Equal(8, exit);
            Assert.Contains(Path.Combine(effectiveRoot, "local-node.db"), stderr.ToString(), StringComparison.Ordinal);

            var identities = Directory.EnumerateFiles(
                    testRoot, "*.identity", SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "install-identities",
                    StringComparison.Ordinal))
                .ToArray();
            var rootSeeds = Directory.EnumerateFiles(
                testRoot, "*.dpapi", SearchOption.AllDirectories).ToArray();
            var violations = new List<string>();

            if (resolver.Roots.Count != 1 || resolver.Roots[0] != effectiveRoot)
            {
                violations.Add(
                    $"Expected one resolver invocation for '{effectiveRoot}'; actual: " +
                    $"[{string.Join(", ", resolver.Roots.Select(root => $"'{root}'"))}].");
            }
            if (identities.Length != 1 || !IsUnder(effectiveRoot, identities[0]))
            {
                violations.Add(
                    $"Expected one install identity under '{effectiveRoot}'; actual: " +
                    $"[{string.Join(", ", identities.Select(path => $"'{path}'"))}].");
            }
            if (rootSeeds.Length != 1 || !IsUnder(effectiveRoot, rootSeeds[0]))
            {
                violations.Add(
                    $"Expected one DPAPI seed under '{effectiveRoot}'; actual: " +
                    $"[{string.Join(", ", rootSeeds.Select(path => $"'{path}'"))}].");
            }

            foreach (var nonSelectedRoot in candidateRoots.Where(root => root != effectiveRoot))
            {
                var entries = Directory.EnumerateFileSystemEntries(
                    nonSelectedRoot, "*", SearchOption.AllDirectories).ToArray();
                if (entries.Length != 0)
                {
                    violations.Add(
                        $"Expected non-selected root '{nonSelectedRoot}' to be empty; actual: " +
                        $"[{string.Join(", ", entries.Select(path => $"'{path}'"))}].");
                }
            }
            Assert.Empty(violations);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LocalNode__DataDirectory", priorDataDirectory);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", priorRootSeed);
            Environment.SetEnvironmentVariable("TEMP", priorTemp);
            Environment.SetEnvironmentVariable("TMP", priorTmp);
            Delete(testRoot);
        }
    }

    [Fact]
    public async Task Recovery_refuses_without_an_explicit_confirmation()
    {
        // "Alarming on use" starts with not being runnable by accident.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await AdministratorRecoveryCommand.RunAsync(
            [AdministratorRecoveryCommand.Verb, "--data-dir", Path.GetTempPath()], stdout, stderr, TimeProvider.System);

        Assert.Equal(2, exit);
        Assert.Contains("recovery_not_confirmed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task Recovery_refuses_while_the_node_is_running()
    {
        // The clause says "with the node stopped". A running node holds node.lock for its lifetime, so this
        // is a proven precondition rather than an instruction in a runbook — and it also means recovery
        // cannot race a live node's own installer state gate.
        var directory = NewDirectory();
        try
        {
            // A store must exist, or the store-missing refusal fires first (and correctly).
            await File.WriteAllTextAsync(Path.Combine(directory, "local-node.db"), string.Empty);
            var storeBefore = await File.ReadAllBytesAsync(Path.Combine(directory, "local-node.db"));
            using var nodeIsRunning = NodeRunLock.TryAcquire(directory);
            Assert.NotNull(nodeIsRunning);

            using var offlineFactory = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
                directory, out var lockFailure, createDirectory: false);
            Assert.Null(offlineFactory);
            Assert.Equal(NodeRunLockFailure.Locked, lockFailure);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await AdministratorRecoveryCommand.RunAsync(
                [AdministratorRecoveryCommand.Verb, "--confirm", "--data-dir", directory], stdout, stderr, TimeProvider.System);

            Assert.Equal(3, exit);
            Assert.Contains("recovery_node_running", stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(storeBefore, await File.ReadAllBytesAsync(Path.Combine(directory, "local-node.db")));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recovery_refuses_a_directory_with_no_node_store_instead_of_creating_one()
    {
        // THE BRICK THIS REPLACES. The run lock called Directory.CreateDirectory first, so a mis-resolved
        // data directory was CREATED, migrated into a fresh schema, given an administrator, and reported as
        // a success — while the real node stayed locked out with none. Recovery repairs an install; it does
        // not build one, and it says which directory it resolved and where it looked.
        var directory = NewDirectory();
        try
        {
            var absent = Path.Combine(directory, "not-the-node");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await AdministratorRecoveryCommand.RunAsync(
                [AdministratorRecoveryCommand.Verb, "--confirm", "--data-dir", absent], stdout, stderr, TimeProvider.System);

            Assert.Equal(6, exit);
            Assert.Contains("recovery_store_missing", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("local-node.db", stderr.ToString(), StringComparison.Ordinal);
            Assert.Empty(stdout.ToString());
            // Nothing was created — not the directory, not a store, not a lock file.
            Assert.False(Directory.Exists(absent));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recovery_exits_with_a_stable_code_rather_than_a_stack_trace_when_the_store_will_not_open()
    {
        // On the shipping Harborline App path the store is keyed with the injected Store DEK verbatim; recovery
        // hardcoded the HKDF branch and died on an unhandled InvalidKeyException with a raw stack trace.
        // Whatever the reason a store will not open, the operator gets a code and a sentence.
        var directory = NewDirectory();
        try
        {
            // A file that is named like the node store and is not one.
            await File.WriteAllTextAsync(
                Path.Combine(directory, "local-node.db"), "this is not a SQLite database");
            Environment.SetEnvironmentVariable(
                "LocalNode__RootSeedHex", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await AdministratorRecoveryCommand.RunAsync(
                [AdministratorRecoveryCommand.Verb, "--confirm", "--data-dir", directory], stdout, stderr, TimeProvider.System);

            Assert.Equal(8, exit);
            Assert.Contains("recovery_store_unusable", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", null);
            Delete(directory);
        }
    }

    [Fact]
    public async Task ConfiguredDataDirectory_OwnsRecoveryIdentityAndKeystore()
    {
        var directory = NewDirectory();
        var priorDataDirectory = Environment.GetEnvironmentVariable("LocalNode__DataDirectory");
        var priorRootSeed = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "local-node.db"), "this is not a SQLite database");
            Environment.SetEnvironmentVariable("LocalNode__DataDirectory", directory);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", null);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await AdministratorRecoveryCommand.RunAsync(
                [AdministratorRecoveryCommand.Verb, "--confirm"],
                stdout,
                stderr,
                TimeProvider.System);

            Assert.Equal(8, exit);
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(directory, "install-identities"), "*.identity"));
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(directory, "keys"), "*.dpapi"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LocalNode__DataDirectory", priorDataDirectory);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", priorRootSeed);
            Delete(directory);
        }
    }

    [Fact]
    public void The_recovery_candidate_resolves_its_tenant_the_way_the_host_does()
    {
        // Recovery resolved the tenant by calling GenesisTeamId.Resolve(seed, ENV VAR ONLY, multiTeam: null)
        // — skipping the multi-team rung that GenesisTeamId.cs documents as the bug, and never reading
        // LocalNode:TeamId from appsettings. On the committed dogfood config that establishes on a tenant
        // the node does not serve, and the per-tenant gate then seals that tenant forever. The derivation
        // now takes the resolved tenant as an argument, so the caller resolves it exactly once, exactly the
        // way the composition root does.
        var seed = RandomNumberGenerator.GetBytes(32);
        var multiTeam = new MultiTeamOptions { Enabled = true };
        multiTeam.TeamBootstraps.Add(new TeamBootstrap
        {
            TeamId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        });

        var resolved = GenesisTeamId.Resolve(seed, configuredTeamId: null, multiTeam);

        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), resolved.Value);
        Assert.NotEqual(GenesisTeamId.Derive(seed).Value, resolved.Value);
        Assert.Equal(
            resolved.Value.ToString("D"),
            AdministratorRecoveryCommand.DeriveGenesisCandidate(seed, resolved.Value).TeamId);
    }

    [Fact]
    public void The_run_lock_is_exclusive_and_released_on_dispose()
    {
        var directory = NewDirectory();
        try
        {
            var first = NodeRunLock.TryAcquire(directory);
            Assert.NotNull(first);
            Assert.False(NodeRunLock.IsFree(directory));

            first!.Dispose();

            // Released by disposal — and, in production, by the kernel however the process dies. A pid file
            // that outlived a crash would lock recovery out permanently, which is the brick this avoids.
            Assert.True(NodeRunLock.IsFree(directory));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recovery_scope_refuses_mutation_after_disposal_with_stable_code()
    {
        var directory = NewDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var contexts = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await contexts.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();

            var scope = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
                directory, out var failure, createDirectory: false);
            Assert.NotNull(scope);
            Assert.Equal(NodeRunLockFailure.None, failure);
            scope!.Dispose();

            var seed = RandomNumberGenerator.GetBytes(32);
            var candidate = AdministratorRecoveryCommand.DeriveGenesisCandidate(
                seed, GenesisTeamId.Derive(seed).Value);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.RecoverAsync(contexts, TimeProvider.System, candidate));
            Assert.Equal(NodeAdministratorAuthority.RecoveryScopeUnavailableCode, error.Message);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void The_recovery_candidate_is_the_signed_genesis_admission_and_is_seed_deterministic()
    {
        // Recovery re-establishes the SAME party under the SAME genesis self-admission rather than minting a
        // new identity, so it does not fork the node's trust roster. And it carries both roster fields, so it
        // cannot produce the unsigned establishment this migration removes.
        var seed = RandomNumberGenerator.GetBytes(32);
        var team = GenesisTeamId.Derive(seed).Value;

        var first = AdministratorRecoveryCommand.DeriveGenesisCandidate(seed, team);
        var second = AdministratorRecoveryCommand.DeriveGenesisCandidate(seed, team);

        Assert.Equal(first, second);
        Assert.False(string.IsNullOrWhiteSpace(first.MemberPublicKey));
        Assert.False(string.IsNullOrWhiteSpace(first.AdmissionSignature));
        Assert.True(first.IsGenesisAdmission);

        var otherSeed = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(
            first.PartyId,
            AdministratorRecoveryCommand
                .DeriveGenesisCandidate(otherSeed, GenesisTeamId.Derive(otherSeed).Value).PartyId);
    }

    [Fact]
    public void The_recovery_candidate_signs_the_same_genesis_admission_the_host_seeds()
    {
        // Recovery called MemberRoster.StableGenesis WITHOUT founderDmPublicKey, which Program.cs (~518)
        // supplies. The RecordId matched, so nothing looked wrong — but the SIGNATURE differed, which made
        // a recovered administrator's AdmissionSignature a correct signature over a record that is not this
        // node's genesis admission. Compared against the host's own construction, not against a literal.
        var seed = RandomNumberGenerator.GetBytes(32);
        var team = GenesisTeamId.Derive(seed).Value;

        using var signer = new NodePrincipalSigner(seed);
        // This is the composition root's first-boot party derivation.  The signed roster is constructed
        // from it and persisted by the recovery fixture below; it is deliberately not two matching literals.
        var partyId = FounderTenantMembershipAttachService.DerivePrincipal(
            ActiveTeamTenantContext.ProjectTenantId(new TeamId(team)),
            InstallationFounderBootstrapCeremony.CorrelationId).Value;
        var founderDmPublicKey = PrincipalId
            .FromBytes(NodeDmKeyDerivation.DeriveDmPublicKey(seed, team.ToString("D")))
            .ToBase64Url();
        var hostRoster = MemberRoster.StableGenesis(
            teamId: team,
            founderPartyId: partyId,
            founderSigner: signer.Signer,
            verifier: new Ed25519Verifier(),
            founderDmPublicKey: founderDmPublicKey);
        var hostGenesis = hostRoster.Find(partyId);
        Assert.NotNull(hostGenesis);

        var candidate = AdministratorRecoveryCommand.DeriveGenesisCandidate(seed, team);

        Assert.Equal(hostGenesis!.Admission.Signature, candidate.AdmissionSignature);
        Assert.Equal(hostGenesis.PublicKey.ToBase64Url(), candidate.MemberPublicKey);
        Assert.Equal(partyId, candidate.PartyId);

        // And it is genuinely a different signature from the one the DM-key-less construction produced,
        // so this assertion is not tautological.
        var withoutDmKey = MemberRoster.StableGenesis(
            teamId: team,
            founderPartyId: partyId,
            founderSigner: signer.Signer,
            verifier: new Ed25519Verifier());
        Assert.NotEqual(
            withoutDmKey.Find(partyId)!.Admission.Signature, candidate.AdmissionSignature);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_run_lock_distinguishes_a_running_node_from_a_missing_directory(bool createDirectory)
    {
        // node.lock was a denial-of-recovery primitive: FileShare.None conflicts with ANY handle, and a
        // read-only directory, a permission-denied directory and a live node all reported
        // administrator.recovery_node_running — sending the operator to stop a service that was not
        // running. The failure modes are now distinct, and recovery maps them to distinct exit codes.
        var absent = Path.Combine(
            Path.GetTempPath(), "harborline-admin-recovery-absent-" + Guid.NewGuid().ToString("N"));

        var handle = NodeRunLock.TryAcquire(absent, out var failure, createDirectory);
        try
        {
            if (createDirectory)
            {
                Assert.NotNull(handle);
                Assert.Equal(NodeRunLockFailure.None, failure);
            }
            else
            {
                // Recovery passes createDirectory:false precisely so a wrong path is REJECTED rather than
                // created, migrated, and reported as a successful recovery of a store nothing else uses.
                Assert.Null(handle);
                Assert.Equal(NodeRunLockFailure.DirectoryMissing, failure);
                Assert.False(Directory.Exists(absent));
            }
        }
        finally
        {
            handle?.Dispose();
            Delete(absent);
        }
    }

    [Fact]
    public void A_held_lock_reports_Locked_rather_than_an_undifferentiated_failure()
    {
        var directory = NewDirectory();
        try
        {
            using var held = NodeRunLock.TryAcquire(directory);
            Assert.NotNull(held);

            var second = NodeRunLock.TryAcquire(directory, out var failure);

            Assert.Null(second);
            Assert.Equal(NodeRunLockFailure.Locked, failure);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recovery_writes_recovery_provenance_and_leaves_the_installer_sealed()
    {
        // The end-to-end shape of the command's write, driven against a real store: an installation that has
        // used its installer and then lost every usable administrator is recovered by the recovery path
        // alone, and the installer stays refused afterwards.
        var directory = NewDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var contexts = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await contexts.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var seed = RandomNumberGenerator.GetBytes(32);
            var candidate = AdministratorRecoveryCommand
                .DeriveGenesisCandidate(seed, GenesisTeamId.Derive(seed).Value);
            var authority = new NodeAdministratorAuthority(
                contexts, TimeProvider.System, TestAuthorization.AllowGate());

            // The installer runs once, then the administrator expires out of usability.
            Assert.True((await authority.EstablishByInstallerAsync(candidate, null)).Applied);
            await ExpireEveryAdministratorAsync(contexts);
            Assert.Empty(await authority.UsableAsync());

            // The installer is now refused even though its state gate reads open.
            Assert.Equal(
                AdministratorAuthorityOutcome.InstallerSealed,
                (await authority.EstablishByInstallerAsync(candidate, null)).Outcome);

            // Recovery is the way back, under its own provenance.
            using var recovery = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
                directory, out var recoveryFailure, createDirectory: false);
            Assert.NotNull(recovery);
            Assert.Equal(NodeRunLockFailure.None, recoveryFailure);
            var recovered = await recovery!.RecoverAsync(contexts, TimeProvider.System, candidate);
            Assert.True(recovered.Applied);
            await using var authorityContext = await contexts.CreateDbContextAsync();
            var authorityRecord = await authorityContext.AdministratorAuthority.AsNoTracking()
                .SingleAsync(record =>
                    record.Event == AdministratorAuthorityEvent.Established &&
                    record.Provenance == AdministratorProvenance.Recovery);
            Assert.Equal(candidate.PartyId, authorityRecord.PartyId);
            Assert.Equal(
                FounderTenantMembershipAttachService.DerivePrincipal(
                    ActiveTeamTenantContext.ProjectTenantId(
                        new TeamId(GenesisTeamId.Derive(seed).Value)),
                    InstallationFounderBootstrapCeremony.CorrelationId).Value,
                authorityRecord.PartyId);
            var recoveredAdministrator = Assert.Single(await authority.UsableAsync());
            Assert.Equal(authorityRecord.PartyId, recoveredAdministrator.PartyId);

            // The recovered authority is readable through the same principal key a gate decides on.
            var gate = TestAuthorization.Gate(request =>
                request.Principal.Value == recoveredAdministrator.PartyId);
            var decision = await gate.DecideAsync(
                TestAuthorization.Write(
                    new TenantId(candidate.TeamId), recoveredAdministrator.PartyId)
                .Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead), "record", "recovery"));
            Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
            Assert.Equal(
                AdministratorProvenance.Recovery,
                recoveredAdministrator.Provenance);

            // And it did NOT re-arm the installer.
            Assert.True(await authority.InstallerHasRunAsync());
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// Force every administrator out of usability by appending an already-past expiry directly. The public
    /// API refuses to do this to the last one — which is the invariant working — so the loss this simulates
    /// is store corruption or a clock move, i.e. exactly what recovery exists for.
    /// </summary>
    private static async Task ExpireEveryAdministratorAsync(
        IDbContextFactory<NodeLocalRosterDbContext> contexts)
    {
        await using var context = await contexts.CreateDbContextAsync();
        var log = await context.AdministratorAuthority.AsNoTracking()
            .OrderBy(record => record.Sequence).ToArrayAsync();
        var tip = log[^1];
        foreach (var administrator in log
            .Where(record => record.Event == AdministratorAuthorityEvent.Established)
            .Select(record => (record.TeamId, record.PartyId))
            .Distinct())
        {
            var expired = new AdministratorAuthorityRecord
            {
                Sequence = tip.Sequence + 1,
                TeamId = administrator.TeamId,
                PartyId = administrator.PartyId,
                Event = AdministratorAuthorityEvent.Established,
                Provenance = AdministratorProvenance.Bootstrap,
                MemberPublicKey = "cHVibGljLWtleQ",
                AdmissionSignature = "c2lnbmF0dXJl",
                AdmittedByPublicKey = "cHVibGljLWtleQ",
                AdmittedByPartyId = administrator.PartyId,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                Reason = "test-expiry",
                PreviousHash = tip.Hash,
                Hash = string.Empty,
            };
            expired.Hash = AdministratorAuthorityRecord.ComputeHash(expired);
            context.AdministratorAuthority.Add(expired);
            tip = expired;
        }

        await context.SaveChangesAsync();
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "harborline-admin-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A held SQLite handle on Windows is not a test failure.
        }
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class RecordingInstallationResolver : AdministratorRecoveryCommand.IInstallationResolver
    {
        private readonly string _defaultRoot;

        internal RecordingInstallationResolver(string defaultRoot)
        {
            _defaultRoot = defaultRoot;
        }

        internal List<string?> Roots { get; } = [];

        public AdministratorRecoveryCommand.ResolvedRecoveryInstallation Resolve(string? effectiveDataRoot)
        {
            var resolvedRoot = effectiveDataRoot ?? _defaultRoot;
            Roots.Add(resolvedRoot);
            return AdministratorRecoveryCommand.DefaultInstallationResolver.Instance.Resolve(resolvedRoot);
        }
    }

    public enum RecoveryRootSelection
    {
        ConfigurationOnly,
        CliOnly,
        ConflictingConfigurationAndCli,
    }
}
