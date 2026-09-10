using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.DependencyInjection;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// THE RECOVERY PATH (ADR 0066 clause 8) — an OFFLINE operator command that establishes an administrator when
/// the installation has none. It is what makes the last-administrator invariant (clause 7) and the removal of
/// the always-on mint (clause 9) safe to land together instead of bricking a node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not an HTTP route, and deliberately not part of the operator CLI.</b> <c>Harborline.Api.NodeOperatorCli</c>
/// speaks to a RUNNING node over HTTP; this command requires the opposite. It runs as a subcommand of the host
/// binary itself, dispatched before any host composition, exactly like <c>hash-web-password</c>:
/// <code>Harborline.Api.LocalNodeHost recover-administrator --confirm [--data-dir &lt;path&gt;]</code>
/// Putting it behind a request the network can reach would recreate the standing
/// administrator-creation endpoint the whole migration step exists to remove.
/// </para>
/// <para>
/// <b>Environment-derived authority.</b> Two preconditions, both proven rather than asserted: the caller must
/// be able to take an exclusive lock on <c>node.lock</c> inside the data directory, which simultaneously
/// proves the node is STOPPED and that the caller can write to the directory it owns; and the caller must be
/// able to open the SQLCipher-keyed store, which requires the installation's root seed. Per the local-software
/// trust boundary this grants nothing to an attacker who does not already hold it — an attacker with that
/// access can read any stored credential anyway (Chromium's "all applications must trust the physically-local
/// user").
/// </para>
/// <para>
/// <b>It cannot re-arm the installer.</b> Recovery writes provenance <c>recovery</c> through
/// the factory-confined recovery mutation, a distinct method on a distinct code
/// path. The installer's separate gate additionally refuses whenever a <c>bootstrap</c> establishment has ever
/// been appended, and the log is append-only, so nothing this command does — and nothing any removal does —
/// can make the installer mintable again.
/// </para>
/// </remarks>
public static class AdministratorRecoveryCommand
{
    /// <summary>The subcommand verb.</summary>
    public const string Verb = "recover-administrator";

    /// <summary>
    /// Runs the offline recovery. Returns a process exit code: 0 on success, non-zero on any refusal, and
    /// NEVER a stack trace — every failure below is one of these stable codes.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <item><term>0</term><description><c>administrator.recovered</c></description></item>
    ///   <item><term>2</term><description><c>administrator.recovery_not_confirmed</c></description></item>
    ///   <item><term>3</term><description><c>administrator.recovery_node_running</c></description></item>
    ///   <item><term>4</term><description><c>administrator.recovery_root_seed_*</c> / <c>store_key_invalid</c></description></item>
    ///   <item><term>5</term><description><c>administrator.recovery_declined</c> (the authority refused)</description></item>
    ///   <item><term>6</term><description><c>administrator.recovery_store_missing</c></description></item>
    ///   <item><term>7</term><description><c>administrator.recovery_directory_unwritable</c></description></item>
    ///   <item><term>8</term><description><c>administrator.recovery_store_unusable</c></description></item>
    ///   <item><term>9</term><description><c>administrator.recovery_configuration_unreadable</c></description></item>
    /// </list>
    /// </remarks>
    /// <param name="args">The full argument vector, with <see cref="Verb"/> at index 0.</param>
    /// <param name="stdout">Success output.</param>
    /// <param name="stderr">Refusal output.</param>
    /// <param name="cancellationToken">Cancels the recovery.</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
                args,
                stdout,
                stderr,
                timeProvider,
                DefaultInstallationResolver.Instance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        TimeProvider timeProvider,
        IInstallationResolver installationResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(installationResolver);

        var confirmed = args.Any(argument =>
            string.Equals(argument, "--confirm", StringComparison.Ordinal));

        // Loud by construction (ADR 0066 clause 8: "permanently audited, and alarming on use"). The operator
        // has to say so explicitly; there is no way to run this by accident inside a script that meant
        // something else.
        if (!confirmed)
        {
            await stderr.WriteLineAsync("administrator.recovery_not_confirmed: this command establishes ADMINISTRATIVE AUTHORITY on " +
                "this installation and is permanently recorded. Re-run with --confirm to proceed.", cancellationToken)
                .ConfigureAwait(false);
            return 2;
        }

        // THE SAME RESOLUTION THE HOST PERFORMS, from the SAME bound configuration. Deriving any of this by
        // hand is how recovery ended up establishing on the wrong team, in the wrong directory, in a store it
        // created itself: the env var alone missed `LocalNode:TeamId` from appsettings, GenesisTeamId.Resolve
        // was called with multiTeam:null (the exact argument GenesisTeamId.cs documents as the bug), and the
        // data directory ignored both the install footprint and `LocalNode:DataDirectory`.
        ResolvedRecoveryConfiguration resolvedConfiguration;
        try
        {
            resolvedConfiguration = ResolveConfiguration(args, installationResolver);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Malformed appsettings, an unbindable value, an unreadable install footprint. The host would
            // fail on the same input — but this command's whole contract is a code and a sentence, so it
            // does not get to be the one place that prints a stack trace at an operator mid-incident.
            await stderr.WriteLineAsync("administrator.recovery_configuration_unreadable: the node's LocalNode configuration could " +
                $"not be resolved, so recovery cannot tell which installation it would repair. " +
                $"{exception.GetType().Name}. Check appsettings and the LocalNode__ environment variables, " +
                "or pass --data-dir explicitly.", cancellationToken)
                .ConfigureAwait(false);
            return 9;
        }

        var options = resolvedConfiguration.Options;
        var dataDirectory = options.DataDirectory;
        var storePath = Path.Combine(dataDirectory, StoreFileName);

        // Recovery REPAIRS an install; it never creates one. Without this, a mis-resolved directory was
        // created by the run lock, migrated into a fresh schema, given an administrator, and reported as a
        // success — while the real node stayed locked out.
        if (!File.Exists(storePath))
        {
            await stderr.WriteLineAsync($"administrator.recovery_store_missing: resolved data directory '{dataDirectory}', expected " +
                $"the node store at '{storePath}', and there is none. Recovery repairs an existing " +
                "installation and will not create one. Pass --data-dir with the node's real data directory.", cancellationToken)
                .ConfigureAwait(false);
            return 6;
        }

        // Precondition 1 — the node is STOPPED and this caller owns the data directory. Held for the whole
        // write, not probed and released, so a node cannot start underneath the recovery. Distinct failure
        // modes get distinct codes: a live node, a directory this account cannot write, and a missing
        // directory used to be one exit code and one (usually wrong) instruction.
        using var recoveryScope = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
            dataDirectory, out var lockFailure, createDirectory: false);
        if (recoveryScope is null)
        {
            var (code, text) = lockFailure switch
            {
                NodeRunLockFailure.DirectoryUnwritable => (7,
                    $"administrator.recovery_directory_unwritable: '{dataDirectory}' exists but this account " +
                    "cannot write it, so ownership of the data directory — the authority this command runs " +
                    "on — is not proven. Run as the account that owns the installation."),
                NodeRunLockFailure.DirectoryMissing => (6,
                    $"administrator.recovery_store_missing: '{dataDirectory}' does not exist. Recovery " +
                    "repairs an existing installation and will not create one."),
                _ => (3,
                    "administrator.recovery_node_running: another process holds " +
                    $"{NodeRunLock.PathFor(dataDirectory)}. Stop the node before recovering an administrator."),
            };
            await stderr.WriteLineAsync(text, cancellationToken).ConfigureAwait(false);
            return code;
        }

        byte[] rootSeed;
        try
        {
            rootSeed = await ResolveRootSeedAsync(resolvedConfiguration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync("administrator.recovery_root_seed_unavailable: the installation root seed could not be " +
                $"resolved, so the encrypted store cannot be opened. {exception.GetType().Name}", cancellationToken)
                .ConfigureAwait(false);
            return 4;
        }

        if (rootSeed.Length != 32)
        {
            await stderr.WriteLineAsync($"administrator.recovery_root_seed_invalid: expected a 32-byte root seed, got {rootSeed.Length}.", cancellationToken)
                .ConfigureAwait(false);
            return 4;
        }

        // The team recovery establishes on, resolved through the SINGLE source of truth with the SAME three
        // rungs Program.cs uses — configured pin, then the first multi-team bootstrap, then the seed-derived
        // genesis. Passing multiTeam:null here skipped rung 2, which on the committed dogfood config
        // establishes on a team the node does not serve; the per-team gate then seals that team forever.
        var teamId = GenesisTeamId.Resolve(rootSeed, options.TeamId, options.MultiTeam).Value;

        // BOTH key paths, chosen the way the host chooses (Program.cs ~1056): when the Tauri shell injected a
        // resolved Store DEK the database is keyed with it VERBATIM. Hardcoding the HKDF branch made recovery
        // unusable on the shipping Harborline App install — it died on an unhandled InvalidKeyException with a raw
        // stack trace, on the one path an operator reaches it from.
        LocalNodeKeyHierarchy keyHierarchy;
        try
        {
            keyHierarchy = LocalNodeKeyHierarchy.Resolve(options, rootSeed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync("administrator.recovery_store_key_invalid: LocalNode:StoreDekHex is configured but could not " +
                $"be used as a store key. {exception.GetType().Name}", cancellationToken)
                .ConfigureAwait(false);
            return 4;
        }

        var services = new ServiceCollection();
        if (!string.IsNullOrWhiteSpace(options.StoreDekHex))
        {
            services.AddSqlCipherLocalNodeDbContextWithStoreDek(
                storeDek: keyHierarchy.AtRestRootKey.Span,
                databasePath: storePath);
        }
        else
        {
            services.AddSqlCipherLocalNodeDbContext(
                rootSeed: rootSeed,
                databasePath: storePath,
                keyDerivation: new SqlCipherKeyDerivation());
        }

        await using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();

        // Every store failure from here on exits with a STABLE CODE. A wrong key, a locked file, a failed
        // migration and a corrupt page all used to escape as an unhandled exception and a stack trace.
        AdministratorCandidate candidate;
        AdministratorAuthorityResult result;
        try
        {
            await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                // The node may never have started since this table was introduced, so bring the roster schema
                // up before writing to it. Migrating only this context is deliberate: recovery must not
                // silently apply unrelated schema on a store the operator has not started.
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            candidate = DeriveGenesisCandidate(rootSeed, teamId);
            result = await recoveryScope.RecoverAsync(
                    contextFactory, timeProvider, candidate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"administrator.recovery_store_unusable: '{storePath}' could not be opened, migrated or " +
                $"written. {exception.GetType().Name}. Check that this is the node's real data directory and " +
                "that the installation's key material is available to this account.", cancellationToken)
                .ConfigureAwait(false);
            return 8;
        }

        if (!result.Applied)
        {
            await stderr.WriteLineAsync($"administrator.recovery_declined: {result.Code}. Nothing was written. Tenant " +
                $"{candidate.TeamId} already has a usable administrator, or the candidate carried no signed " +
                "admission.", cancellationToken)
                .ConfigureAwait(false);
            return 5;
        }

        await stdout.WriteLineAsync($"administrator.recovered: '{candidate.PartyId}' now holds administrative authority over tenant " +
            $"{candidate.TeamId} in '{storePath}' with provenance RECOVERY (sequence {result.Sequence}). " +
            "This is permanently recorded in the node's administrator-authority log and cannot be removed " +
            "while it is that tenant's last usable administrator. The installer principal remains sealed " +
            "and was NOT re-armed.", cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    /// <summary>The node store file name inside the data directory — the same one the host keys and opens.</summary>
    private const string StoreFileName = "local-node.db";

    /// <summary>
    /// Bind <c>LocalNode</c> the way the host binds it: appsettings (+ environment overlay), environment
    /// variables, then the durable install footprint. Recovery must resolve the SAME data directory, the
    /// SAME team and the SAME store key the node would, or it repairs something that is not the node.
    /// </summary>
    private static ResolvedRecoveryConfiguration ResolveConfiguration(
        IReadOnlyList<string> args,
        IInstallationResolver installationResolver)
    {
        var environmentName =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        // The host's content root is the current directory; the binary's own directory is added first so a
        // service started from elsewhere still reads its shipped appsettings, and the current directory
        // still wins where the two differ.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, $"appsettings.{environmentName}.json"), optional: true)
            .AddJsonFile(
                Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true)
            .AddJsonFile(
                Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{environmentName}.json"),
                optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new LocalNodeOptions();
        configuration.GetSection("LocalNode").Bind(options);

        // Select the installation root before constructing any identity, footprint, keystore, seed slot, or
        // database path. The CLI is the final operator override; every downstream owner receives that same root.
        var effectiveDataRoot = ValueOf(args, "--data-dir") ?? configuration["LocalNode:DataDirectory"];
        if (effectiveDataRoot is not null)
            options.DataDirectory = effectiveDataRoot;

        var installation = new Lazy<ResolvedRecoveryInstallation>(
            () => installationResolver.Resolve(effectiveDataRoot),
            LazyThreadSafetyMode.ExecutionAndPublication);
        if (effectiveDataRoot is null)
            options.ApplyInstallFootprint(installation.Value.Footprint, dataDirectoryIsConfigured: false);

        return new ResolvedRecoveryConfiguration(options, installation);
    }

    internal interface IInstallationResolver
    {
        ResolvedRecoveryInstallation Resolve(string? effectiveDataRoot);
    }

    internal sealed class DefaultInstallationResolver : IInstallationResolver
    {
        internal static DefaultInstallationResolver Instance { get; } = new();

        private DefaultInstallationResolver()
        {
        }

        public ResolvedRecoveryInstallation Resolve(string? effectiveDataRoot)
        {
            return ResolveInstallation(effectiveDataRoot);
        }
    }

    private static ResolvedRecoveryInstallation ResolveInstallation(string? effectiveDataRoot)
    {
        var installFootprintRoot = string.IsNullOrWhiteSpace(effectiveDataRoot)
            ? null
            : effectiveDataRoot;
        var installIdentityPath = string.IsNullOrWhiteSpace(effectiveDataRoot)
            ? null
            : Path.Combine(
                effectiveDataRoot,
                "install-identities",
                Path.GetFileName(InstallIdentityPaths.GetDefaultIdentityFilePath()));
        using var footprintServices = new ServiceCollection()
            .AddHarborlineInstallFootprint(installFootprintRoot, installIdentityPath)
            .BuildServiceProvider();
        var identityProvider = footprintServices.GetRequiredService<IInstallIdentityProvider>();
        var footprintProvider = footprintServices.GetRequiredService<IInstallFootprintProvider>();
        var footprint = footprintProvider
            .GetInstallFootprintAsync(CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new ResolvedRecoveryInstallation(identityProvider, footprintProvider, footprint);
    }

    /// <summary>
    /// Reconstruct the installation's genesis administrator candidate — the same derivation the composition
    /// root performs, so recovery re-establishes the SAME party with the SAME signed genesis self-admission
    /// rather than minting a new identity.
    /// </summary>
    /// <param name="rootSeed">The installation's 32-byte root seed.</param>
    /// <param name="teamId">
    /// The genesis team, resolved by the caller through <see cref="GenesisTeamId.Resolve"/> against the SAME
    /// bound configuration the host uses. It is a parameter rather than a re-derivation precisely because the
    /// re-derivation was wrong: it read one environment variable and passed <c>multiTeam: null</c>.
    /// </param>
    internal static AdministratorCandidate DeriveGenesisCandidate(byte[] rootSeed, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(rootSeed);
        using var signer = new NodePrincipalSigner(rootSeed);
        // The SAME pure derivation Program.cs uses for an unseeded install: project the resolved genesis
        // team to its tenant, then derive the founder's canonical tenant principal from the founder ceremony.
        // Recovery receives the team resolved by GenesisTeamId.Resolve above; it never derives a party from
        // the shell account or node signing key, because no reader grants authority to either key.
        var genesisTenant = ActiveTeamTenantContext.ProjectTenantId(new TeamId(teamId));
        var partyId = FounderTenantMembershipAttachService.DerivePrincipal(
            genesisTenant, InstallationFounderBootstrapCeremony.CorrelationId).Value;

        // The founder's own team-scoped DM public key is SIGNED INTO the genesis admission envelope in
        // production (Program.cs ~518). Omitting it produced a record with the same RecordId and a DIFFERENT
        // signature — a valid signature over a record that is not this node's genesis admission.
        var founderDmPublicKey = PrincipalId
            .FromBytes(NodeDmKeyDerivation.DeriveDmPublicKey(rootSeed, teamId.ToString("D")))
            .ToBase64Url();

        var roster = MemberRoster.StableGenesis(
            teamId: teamId,
            founderPartyId: partyId,
            founderSigner: signer.Signer,
            verifier: new Ed25519Verifier(),
            founderDmPublicKey: founderDmPublicKey);
        var genesis = roster.Find(partyId)
            ?? throw new InvalidOperationException(
                "The reconstructed genesis roster does not contain its own founder.");

        return new AdministratorCandidate(
            TeamId: teamId.ToString("D"),
            PartyId: partyId,
            MemberPublicKey: genesis.PublicKey.ToBase64Url(),
            AdmissionSignature: genesis.Admission.Signature,
            AdmittedByPublicKey: genesis.Admission.AdmittedByPublicKey,
            AdmittedByPartyId: genesis.Admission.AdmittedByPartyId,
            IsGenesisAdmission: genesis.Admission.IsGenesis);
    }

    private static async Task<byte[]> ResolveRootSeedAsync(
        ResolvedRecoveryConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var injected = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        if (!string.IsNullOrWhiteSpace(injected))
        {
            return Convert.FromHexString(injected);
        }

        var installation = configuration.Installation.Value;
        return await InstallRootSeedResolver.ResolveAsync(
                installation.IdentityProvider,
                installation.FootprintProvider,
                installation.Footprint.KeystoreDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record ResolvedRecoveryConfiguration(
        LocalNodeOptions Options,
        Lazy<ResolvedRecoveryInstallation> Installation);

    internal sealed record ResolvedRecoveryInstallation(
        IInstallIdentityProvider IdentityProvider,
        IInstallFootprintProvider FootprintProvider,
        InstallFootprint Footprint);

    private static string? ValueOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

}
