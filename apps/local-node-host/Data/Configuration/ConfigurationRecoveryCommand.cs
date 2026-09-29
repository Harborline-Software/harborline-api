using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Kernel.Core;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>
/// The OFFLINE configuration-recovery path (T-587, T-909 slice 6): brings a prepared generation, the effective
/// pointer and the evidence outbox a crash left behind to a terminal state through the platform kernel's
/// <see cref="ConfigurationRecovery"/>, or refuses before any write naming the state.
/// </summary>
/// <remarks>
/// <code>Harborline.Api.LocalNodeHost recover-configuration --reason "&lt;why&gt;" [--tenant &lt;key&gt;] [--data-dir &lt;path&gt;]</code>
/// Dispatched before any host composition, like <see cref="AdministratorRecoveryCommand"/>, and for the same
/// reason: it must work when the Configuration Management package is broken, so it is not an HTTP route gated
/// on pack-installed permissions. Its authority is ownership of the data directory with the node stopped (ADR
/// 0066 clause 8): the exclusive <c>node.lock</c> plus the key material that opens the encrypted store.
/// It reads only the kernel profile rows; no catalogue, pack or released definition is consulted.
/// </remarks>
public static class ConfigurationRecoveryCommand
{
    /// <summary>The subcommand verb.</summary>
    public const string Verb = "recover-configuration";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the offline recovery. Exit codes: 0 recovered; 3 node running; 4 root seed or store key unusable;
    /// 5 the kernel refused (code and state printed, nothing written); 6 store missing; 7 data directory
    /// unwritable; 8 store unusable; 9 configuration unreadable.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
        TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(timeProvider);

        LocalNodeOptions options;
        AdministratorRecoveryCommand.ResolvedRecoveryConfiguration resolved;
        try
        {
            resolved = AdministratorRecoveryCommand.ResolveConfiguration(args, AdministratorRecoveryCommand.DefaultInstallationResolver.Instance);
            options = resolved.Options;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"configuration.recovery_configuration_unreadable: {exception.GetType().Name}. " +
                "Check appsettings and the LocalNode__ environment variables, or pass --data-dir.", cancellationToken).ConfigureAwait(false);
            return 9;
        }

        var dataDirectory = options.DataDirectory;
        var storePath = Path.Combine(dataDirectory, AdministratorRecoveryCommand.StoreFileName);
        if (!File.Exists(storePath))
        {
            await stderr.WriteLineAsync($"configuration.recovery_store_missing: no node store at '{storePath}'. Recovery repairs " +
                "an existing installation and will not create one.", cancellationToken).ConfigureAwait(false);
            return 6;
        }

        // Held for the whole write: the node is stopped and this account owns the data directory.
        using var runLock = NodeRunLock.TryAcquire(dataDirectory, out var lockFailure, createDirectory: false);
        if (runLock is null)
        {
            var (code, text) = lockFailure switch
            {
                NodeRunLockFailure.DirectoryUnwritable => (7, $"configuration.recovery_directory_unwritable: '{dataDirectory}' is not writable by this account."),
                NodeRunLockFailure.DirectoryMissing => (6, $"configuration.recovery_store_missing: '{dataDirectory}' does not exist."),
                _ => (3, $"configuration.recovery_node_running: another process holds {NodeRunLock.PathFor(dataDirectory)}. Stop the node first."),
            };
            await stderr.WriteLineAsync(text, cancellationToken).ConfigureAwait(false);
            return code;
        }

        byte[] rootSeed;
        LocalNodeKeyHierarchy keys;
        try
        {
            rootSeed = await AdministratorRecoveryCommand.ResolveRootSeedAsync(resolved, cancellationToken).ConfigureAwait(false);
            keys = LocalNodeKeyHierarchy.Resolve(options, rootSeed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"configuration.recovery_root_seed_unavailable: {exception.GetType().Name}.", cancellationToken).ConfigureAwait(false);
            return 4;
        }

        var services = new ServiceCollection();
        if (!string.IsNullOrWhiteSpace(options.StoreDekHex))
            services.AddSqlCipherLocalNodeDbContextWithStoreDek(storeDek: keys.AtRestRootKey.Span, databasePath: storePath);
        else
            services.AddSqlCipherLocalNodeDbContext(rootSeed: rootSeed, databasePath: storePath, keyDerivation: new SqlCipherKeyDerivation());
        AddEvidenceDelivery(services, rootSeed, timeProvider);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>();

        var tenant = AdministratorRecoveryCommand.ValueOf(args, "--tenant")
            ?? ActiveTeamTenantContext.ProjectTenantId(new TeamId(GenesisTeamId.Resolve(rootSeed, options.TeamId, options.MultiTeam).Value)).Value;
        // The actor is the data directory's owner, proven by the held lock; no shell account is derived here.
        const string actor = "data-directory-owner";
        var snapshot = JsonSerializer.Serialize(new
        {
            authority = "data-directory-ownership",
            dataDirectory,
            runLock = NodeRunLock.PathFor(dataDirectory),
        }, Json);
        var request = new ConfigurationRecoveryRequest(Guid.NewGuid().ToString("N"), tenant, actor,
            AdministratorRecoveryCommand.ValueOf(args, "--reason") ?? string.Empty, snapshot);

        ConfigurationRecoveryResult<ConfigurationRecoveryRecord> result;
        try
        {
            await using (var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            // The kernel audit trail the stranded evidence is appended to lives in the same store.
            await using (var trailContext = await provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>()
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
                await trailContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            result = await RecoverAsync(factory, new KernelClock(timeProvider), request, new DataDirectoryOwnership(runLock),
                provider.GetRequiredService<ConfigurationEvidenceOutbox>(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"configuration.recovery_store_unusable: '{storePath}' could not be opened, migrated or " +
                $"written. {exception.GetType().Name}. Nothing was committed.", cancellationToken).ConfigureAwait(false);
            return 8;
        }

        if (!result.Committed)
        {
            await stderr.WriteLineAsync($"configuration.recovery_refused: {result.Refusal!.Code} (state: {result.Refusal.State}). " +
                "Nothing was written.", cancellationToken).ConfigureAwait(false);
            return 5;
        }

        await stdout.WriteLineAsync($"configuration.recovered: tenant {tenant}, effective generation {result.Record!.EffectiveDigest}; " +
            string.Join(", ", result.Record.Repairs.Select(repair => $"{repair.Residue} {repair.Identity} -> {repair.Terminal}")) +
            $". Recorded as recovery {request.RecoveryId} with its audit.", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The host's own kernel audit trail and principal signer, so a stranded entry reaches the trail the running host
    /// reads, signed by the same node key, through the drain's delivery path.
    /// </summary>
    internal static void AddEvidenceDelivery(IServiceCollection services, byte[] rootSeed, TimeProvider timeProvider)
    {
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddSingleton(new NodePrincipalSigner(rootSeed));
        services.AddSingleton<IOperationSigner>(sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
        services.AddEnrollmentCompensatingControlAudit();
        services.AddSingleton<ConfigurationEvidenceOutbox>();
    }

    /// <summary>The kernel recovery over this host's kernel profile rows, committed through one store transaction.</summary>
    internal static ValueTask<ConfigurationRecoveryResult<ConfigurationRecoveryRecord>> RecoverAsync(
        IDbContextFactory<NodeLocalPacksDbContext> factory, KernelClock clock, ConfigurationRecoveryRequest request,
        IKernelConfigurationRecoveryCapability? capability, ConfigurationEvidenceOutbox evidence, Action<string>? crashPoint = null,
        CancellationToken cancellationToken = default) =>
        new ConfigurationRecovery(new ProfileReader(factory), capability, clock)
            .RecoverAsync(request, new TransactionPort(factory, evidence, request.TenantKey, crashPoint), cancellationToken);

    /// <summary>The capability is the held <c>node.lock</c>: granted exactly while the lock is held.</summary>
    internal sealed class DataDirectoryOwnership(FileStream runLock) : IKernelConfigurationRecoveryCapability
    {
        public ValueTask<bool> CanRecoverAsync(string actorId, string tenantKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!runLock.SafeFileHandle.IsClosed);
    }

    /// <summary>The kernel profile, read from the effective pointer, prepared projection and evidence outbox rows.</summary>
    internal sealed class ProfileReader(IDbContextFactory<NodeLocalPacksDbContext> factory) : IKernelProfileReader
    {
        public async ValueTask<KernelProfileSnapshot?> ReadAsync(string tenantKey, CancellationToken cancellationToken = default)
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var pointer = await context.EffectiveGenerations.AsNoTracking()
                .FirstOrDefaultAsync(row => row.Tenant == tenantKey, cancellationToken).ConfigureAwait(false);
            var outbox = await context.EvidenceOutbox.AsNoTracking().Where(row => row.Tenant == tenantKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            // ponytail: the kernel snapshot holds one prepared residue, so each run abandons the latest one; a
            // second leftover is abandoned by the next run.
            var prepared = (await context.PreparedProjections.AsNoTracking()
                    .Where(row => row.Tenant == tenantKey && row.CandidateDigest != (pointer == null ? "" : pointer.Digest))
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .MaxBy(row => row.PreparedAt);
            if (pointer is null && prepared is null && outbox.Count == 0) return null;
            // The pointer digest is the platform generation digest: sha256 over the canonical reference document,
            // which ReferencesJson stores byte for byte (Digest_the_kernel_verifies_matches_the_host_effective_pointer).
            return new KernelProfileSnapshot(tenantKey,
                pointer is null ? null : new EffectivePointer(pointer.Digest, pointer.EvidenceIntentId),
                pointer is null ? null : Encoding.UTF8.GetBytes(pointer.ReferencesJson),
                prepared is null ? null : new PreparedGenerationResidue(prepared.CandidateDigest, prepared.ProjectionDigest),
                outbox.Select(row => new EvidenceOutboxEntry(row.IntentId, row.PublishedAt is not null)).ToArray());
        }
    }

    /// <summary>One store transaction: the record and its repairs, then the audit, committed together or not at all.</summary>
    internal sealed class TransactionPort(IDbContextFactory<NodeLocalPacksDbContext> factory, ConfigurationEvidenceOutbox evidence,
        string tenant, Action<string>? crashPoint)
        : IKernelTransactionPort<ConfigurationRecoveryRecord, ConfigurationRecoveryRecord>
    {
        public async ValueTask<IKernelTransaction<ConfigurationRecoveryRecord, ConfigurationRecoveryRecord>> BeginAsync(
            KernelOperationIdentity operation, CancellationToken cancellationToken = default)
        {
            // Delivered before the write transaction opens: the trail may share local-node.db, whose write lock the
            // transaction holds. A crash after this append leaves the entry held by its audit id, so the next run or
            // the host's drain finds it and only marks it.
            await evidence.AppendOwedAsync(tenant, cancellationToken).ConfigureAwait(false);
            var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new Transaction(context, transaction, crashPoint);
        }
    }

    private sealed class Transaction(NodeLocalPacksDbContext context, IDbContextTransaction transaction, Action<string>? crashPoint)
        : IKernelTransaction<ConfigurationRecoveryRecord, ConfigurationRecoveryRecord>
    {
        private ConfigurationRecoveryRecord? _record;

        public async ValueTask StageRecordAsync(ConfigurationRecoveryRecord record, CancellationToken cancellationToken = default)
        {
            foreach (var repair in record.Repairs.Where(repair => repair.Terminal == ConfigurationTerminalState.Abandoned))
            {
                var row = await context.PreparedProjections.FindAsync([record.TenantKey, repair.Identity], cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("configuration-prepared-residue-moved");
                context.PreparedProjections.Remove(row);
            }
            // The Confirmed pointer is never moved; an earlier generation becomes effective only by a new activation.
            context.Recoveries.Add(new ConfigurationRecoveryRow
            {
                Tenant = record.TenantKey, RecoveryId = record.RecoveryId, EffectiveDigest = record.EffectiveDigest,
                Reason = record.Reason, AuthoritySnapshot = record.AuthoritySnapshot,
                RecordJson = JsonSerializer.Serialize(record, Json),
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            crashPoint?.Invoke("record-written");
            _record = record;
        }

        public async ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default)
        {
            var record = _record ?? throw new InvalidOperationException("configuration-recovery-record-not-staged");
            // Publication takes the audit's instant: one clock read for everything this recovery persists.
            // A row from before the authority column has nothing to append; this audit is its stand-in and names it.
            var standInFor = new JsonArray();
            foreach (var repair in record.Repairs.Where(repair => repair.Terminal == ConfigurationTerminalState.Published))
            {
                var row = await ConfigurationEvidenceOutbox.MarkPublishedAsync(context, record.TenantKey, repair.Identity,
                    audit.RecordedAt, cancellationToken).ConfigureAwait(false);
                if (row.AuthoritySnapshotJson is null) standInFor.Add(repair.Identity);
            }
            var payload = JsonNode.Parse(audit.Payload.Span)!.AsObject();
            payload["standInFor"] = standInFor;
            context.RecoveryAudit.Add(new ConfigurationRecoveryAuditRow
            {
                AuditId = audit.AuditId, Tenant = record.TenantKey, ActorId = audit.ActorId, RecordedAt = audit.RecordedAt,
                PayloadJson = payload.ToJsonString(),
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            crashPoint?.Invoke("audit-written");
        }

        public async ValueTask<ConfigurationRecoveryRecord> CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return _record!;
        }

        public async ValueTask RollbackAsync(CancellationToken cancellationToken = default) =>
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
