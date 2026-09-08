using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Durable, file-backed <see cref="ITenantGovernanceStateStore"/> — persists the per-tenant
/// <see cref="TenantGovernanceState"/> (the instance lifecycle <see cref="SetupPhase"/>) as a single JSON
/// document under the node's data directory. Survives restarts, so the founder's declared "start running"
/// milestone (and the fresh-instance setup phase) persist without re-showing the setup chrome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file store, not an EF table (for B5).</b> The phase is <em>chrome prominence, not authority</em>
/// (ADR 0144 AD.1) — non-sensitive, non-PII, one value per tenant. A dedicated JSON document keeps the
/// slice self-contained (no shared EF migration / model-snapshot edit that would collide with the parallel
/// build lanes) while still being genuinely durable. When ADR 0144 Phase-2 builds the canonical
/// <c>TenantGovernanceState</c> in <c>foundation-authorization</c>, this reconciles into that record — the
/// reconciliation seam is documented in the B5 council-request beacon. (Reseed / restore-from-backup that
/// misses this file is a benign degradation: Build simply becomes prominent again and the founder re-locks
/// — no data loss, no authority change.)
/// </para>
/// <para>
/// <b>Atomic + crash-safe writes.</b> Every mutation writes the full document to a sibling temp file and
/// atomically renames it over the target (<see cref="File.Move(string, string, bool)"/>), so a truncated
/// write / yanked-power mid-save never leaves a half-written document — the prior document survives intact.
/// </para>
/// <para>
/// <b>Serialized mutations.</b> A single-writer <see cref="SemaphoreSlim"/> serializes read-modify-write
/// cycles so a concurrent <see cref="EnsureGenesisAsync"/> + <see cref="FinishSetupAsync"/> cannot race a
/// lost update. Node loopback traffic is low-volume single-operator; this is defence-in-depth.
/// </para>
/// </remarks>
public sealed class FileTenantGovernanceStateStore : ITenantGovernanceStateStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>Construct over the durable document <paramref name="filePath"/> (created on first write).
    /// <paramref name="clock"/> supplies genesis / lock-down timestamps (injectable for tests).</summary>
    public FileTenantGovernanceStateStore(string filePath, Func<DateTimeOffset> clock)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _filePath = filePath;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The canonical document filename under the node data directory.</summary>
    public const string FileName = "governance-lifecycle.json";

    /// <summary>Builds a store rooted at <paramref name="dataDirectory"/> (the node's
    /// <c>LocalNodeOptions.DataDirectory</c>), persisting to <see cref="FileName"/> inside it.</summary>
    public static FileTenantGovernanceStateStore InDirectory(string dataDirectory, Func<DateTimeOffset> clock)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return new FileTenantGovernanceStateStore(Path.Combine(dataDirectory, FileName), clock);
    }

    /// <inheritdoc />
    public async Task<TenantGovernanceState?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            return doc.TryGetValue(tenantId, out var row) ? row.ToState(tenantId) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TenantGovernanceState> EnsureGenesisAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            if (doc.TryGetValue(tenantId, out var existing))
            {
                // Idempotent: never re-set an instance that already has a governance state (any phase).
                return existing.ToState(tenantId);
            }

            var at = _clock();
            var genesis = TenantGovernanceState.Genesis(tenantId, at);
            doc[tenantId] = GovernanceRow.From(genesis);
            await PersistAsync(doc, ct).ConfigureAwait(false);
            return genesis;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TenantGovernanceState> FinishSetupAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            var at = _clock();
            var current = doc.TryGetValue(tenantId, out var row)
                ? row.ToState(tenantId)
                : TenantGovernanceState.Genesis(tenantId, at);

            var next = current.LockedDown(at);
            if (next == current && doc.ContainsKey(tenantId))
            {
                // Already operating and already persisted — a genuine no-op (idempotent re-declare).
                return current;
            }

            doc[tenantId] = GovernanceRow.From(next);
            await PersistAsync(doc, ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _mutex.Release();
        }
    }

    // ── Persistence (atomic temp+rename) ────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, GovernanceRow>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, GovernanceRow>(StringComparer.Ordinal);
        }

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var doc = await JsonSerializer
            .DeserializeAsync<Dictionary<string, GovernanceRow>>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return doc is null
            ? new Dictionary<string, GovernanceRow>(StringComparer.Ordinal)
            : new Dictionary<string, GovernanceRow>(doc, StringComparer.Ordinal);
    }

    private async Task PersistAsync(Dictionary<string, GovernanceRow> doc, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, doc, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // Atomic replace: the prior document survives a crash between write + rename.
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();

    /// <summary>On-disk row shape (phase stored as its wire token so a corrupt value fails closed on read).</summary>
    private sealed record GovernanceRow(
        [property: JsonPropertyName("setupPhase")] string SetupPhase,
        [property: JsonPropertyName("enteredSetupAt")] DateTimeOffset EnteredSetupAt,
        [property: JsonPropertyName("enteredOperatingAt")] DateTimeOffset? EnteredOperatingAt)
    {
        public static GovernanceRow From(TenantGovernanceState state) => new(
            SetupPhase: SetupPhaseWire.ToWire(state.SetupPhase),
            EnteredSetupAt: state.EnteredSetupAt,
            EnteredOperatingAt: state.EnteredOperatingAt);

        public TenantGovernanceState ToState(string tenantId) => new()
        {
            TenantId = tenantId,
            SetupPhase = SetupPhaseWire.FromWire(SetupPhase),
            EnteredSetupAt = EnteredSetupAt,
            EnteredOperatingAt = EnteredOperatingAt,
        };
    }
}
