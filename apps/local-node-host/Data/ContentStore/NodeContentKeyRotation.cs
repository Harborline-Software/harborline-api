using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Harborline.Foundation.ExecutionRuntime;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The scheduled, restartable epoch rewrap and two-phase destruction (ADR 0116 decision 5).</summary>
public sealed class NodeContentKeyRotation(NodeContentStoreStorage storage, ContentKeyRotationSettings settings, TimeProvider clock, ILogger<NodeContentKeyRotation>? logger = null) : IDisposable
{
    /// <summary>The platform trigger kind, matching the staging sweep.</summary>
    public const string TriggerKind = "schedule";

    /// <summary>The platform-owned rotation schedule (T-521 acceptance 3).</summary>
    public const string Schedule = "sys.sched.content-key-rotation";

    private static readonly Action<ILogger, string, Exception?> LogStrayEpoch = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(521, "StrayEpochFile"), "Skipping unrecognized content epoch file {Path}");

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Registers the rotation run exactly as the producer registers its staging sweep.</summary>
    public static RunKindRegistration Register(RunKindRegistry registry) =>
        registry.Register(new("content-key-rotation"), InMemoryContentStore.StoreDeclaration);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Runs a tenant's scheduled rotation; every batch has fresh kernel evidence and its own transaction.</summary>
    public async ValueTask<ContentErasureWindow> RunAsync(TenantId tenant, InMemoryContentStore store,
        Func<ContentStoreWrite> write, int batchSize = 128, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var keys = (NodeContentEpochKeyStore)storage.Epochs;
            var bookkeeping = (NodeContentBookkeeping)storage.Bookkeeping;
            var directory = keys.TenantDirectory(tenant);
            var elapsedPath = Path.Combine(directory, "last-rewrap");
            var elapsed = TimeSpan.FromTicks(await ReadTicksAsync(elapsedPath, cancellationToken).ConfigureAwait(false) ?? 0);
            if (!Directory.Exists(directory)) return settings.ErasureWindow(elapsed);
            var current = await keys.GetCurrentAsync(tenant, cancellationToken).ConfigureAwait(false);
            var epochs = new List<Guid>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.epoch"))
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var epoch)) epochs.Add(epoch);
                else LogStrayEpoch(logger ?? NullLogger<NodeContentKeyRotation>.Instance, path, null);
            }
            if (current is null && epochs.Count == 0) return settings.ErasureWindow(elapsed);
            var created = current is null ? null : await ReadTicksAsync(Path.Combine(directory, current.EpochId.ToString("N") + ".created"), cancellationToken).ConfigureAwait(false);
            var currentSince = current is null ? DateTimeOffset.MinValue : created is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(directory, current.EpochId.ToString("N") + ".epoch")));
            if (current is null || clock.GetUtcNow() - currentSince >= settings.RotationPeriod)
            {
                await store.RotateEpochAsync(tenant, Evidence(), cancellationToken).ConfigureAwait(false);
                current = (await keys.GetCurrentAsync(tenant, cancellationToken).ConfigureAwait(false))!;
                await WriteTicksAsync(Path.Combine(directory, current.EpochId.ToString("N") + ".created"), clock.GetUtcNow().UtcTicks, cancellationToken).ConfigureAwait(false);
            }

            foreach (var epoch in epochs.Where(epoch => epoch != current.EpochId))
            {
                var pendingPath = Path.Combine(directory, epoch.ToString("N") + ".pending");
                var marked = await ReadTicksAsync(pendingPath, cancellationToken).ConfigureAwait(false);
                if (marked is null)
                {
                    var started = clock.GetTimestamp();
                    int moved;
                    do
                    {
                        moved = await store.RewrapEpochAsync(tenant, epoch, current.EpochId, batchSize, Evidence(), cancellationToken).ConfigureAwait(false);
                        NodeContentEpochKeyStore.AfterDurableStep?.Invoke("batch:committed");
                        if (!await bookkeeping.CheckpointAsync(cancellationToken).ConfigureAwait(false)) return settings.ErasureWindow(elapsed);
                        NodeContentEpochKeyStore.AfterDurableStep?.Invoke("batch:checkpoint");
                    } while (moved != 0);
                    elapsed = clock.GetElapsedTime(started);
                    await WriteTicksAsync(elapsedPath, elapsed.Ticks, cancellationToken).ConfigureAwait(false);
                }

                using var destruction = await storage.Backups.TryEnterDestructionAsync(cancellationToken).ConfigureAwait(false);
                if (destruction is null) continue;
                if (marked is null)
                {
                    try
                    {
                        await bookkeeping.RetireUnusedEpochAsync(tenant, epoch, Evidence(), cancellationToken).ConfigureAwait(false);
                        NodeContentEpochKeyStore.AfterDurableStep?.Invoke("retire:fenced");
                    }
                    catch (ContentEpochInUseException)
                    {
                        continue;
                    }
                    if (!await bookkeeping.CheckpointAsync(cancellationToken).ConfigureAwait(false)) continue;
                    NodeContentEpochKeyStore.AfterDurableStep?.Invoke("retire:checkpoint");
                    await WriteTicksAsync(pendingPath, clock.GetUtcNow().UtcTicks, cancellationToken).ConfigureAwait(false);
                    NodeContentEpochKeyStore.AfterDurableStep?.Invoke("retire:pending-written");
                }
                else if (clock.GetUtcNow() - new DateTimeOffset(marked.Value, TimeSpan.Zero) >= settings.WaitingPeriod)
                {
                    if (!await bookkeeping.CheckpointAsync(cancellationToken).ConfigureAwait(false)) continue;
                    await store.DestroyEpochAsync(tenant, epoch, Evidence(), cancellationToken).ConfigureAwait(false);
                    File.Delete(pendingPath);
                    File.Delete(Path.Combine(directory, epoch.ToString("N") + ".created"));
                    NodeContentByteStore.SyncDirectory(directory);
                }
            }
            return settings.ErasureWindow(elapsed);

            ContentStoreWrite Evidence()
            {
                var evidence = write();
                if (evidence.Audit.ActorId != "sys.scheduler" || evidence.Transaction is not null)
                    throw new ArgumentException("Content-key rotation runs as sys.scheduler in standalone transactions.", nameof(write));
                return evidence;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async ValueTask<long?> ReadTicksAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0
                ? ticks : throw new InvalidDataException("Content-key rotation metadata is unreadable.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private ValueTask WriteTicksAsync(string path, long ticks, CancellationToken cancellationToken) =>
        NodeContentEpochKeyStore.WriteDurablyAsync(path, Encoding.ASCII.GetBytes(ticks.ToString(CultureInfo.InvariantCulture)), ((NodeContentEpochKeyStore)storage.Epochs).DirectoryPath, cancellationToken: cancellationToken);
}
