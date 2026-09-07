using System.Text.Json;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Durable, file-backed <see cref="ITenantConsentStore"/> — the tenant consent records live where the
/// tenant's other lifecycle record lives (<see cref="FileTenantGovernanceStateStore"/>): one atomically
/// rewritten JSON document under the node's data directory, keyed tenant → record id. No new store family,
/// and no EF migration (the migration catalog is pinned).
/// </summary>
/// <remarks>
/// Atomic temp+rename writes and a single-writer mutex, for the same reasons the governance-state store has
/// them. The point of the file is the restart proof: authority to act on a subject's data is on DISK, so
/// dropping every process-local object leaves the gate's answer unchanged.
/// </remarks>
public sealed class FileTenantConsentStore : ITenantConsentStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The canonical document filename under the node data directory.</summary>
    public const string FileName = "consent-records.json";

    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>Construct over the durable document <paramref name="filePath"/> (created on first write).</summary>
    public FileTenantConsentStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _filePath = filePath;
    }

    /// <summary>Builds a store rooted at <paramref name="dataDirectory"/>, persisting to <see cref="FileName"/>.</summary>
    public static FileTenantConsentStore InDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return new FileTenantConsentStore(Path.Combine(dataDirectory, FileName));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TenantConsentRecord>> ReadAsync(
        TenantId tenant, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            return doc.TryGetValue(tenant.Value, out var rows)
                ? rows.Values.Select(row => row.ToRecord(tenant)).ToArray()
                : [];
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(TenantConsentRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            if (!doc.TryGetValue(record.Tenant.Value, out var rows))
            {
                rows = new Dictionary<string, ConsentRow>(StringComparer.Ordinal);
                doc[record.Tenant.Value] = rows;
            }

            rows[record.Id] = ConsentRow.From(record);
            await PersistAsync(doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TenantId>> TenantsAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(ct).ConfigureAwait(false);
            return doc.Keys.Select(key => new TenantId(key)).ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, Dictionary<string, ConsentRow>>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new(StringComparer.Ordinal);
        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var doc = await JsonSerializer
            .DeserializeAsync<Dictionary<string, Dictionary<string, ConsentRow>>>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return doc is null ? new(StringComparer.Ordinal) : new(doc, StringComparer.Ordinal);
    }

    private async Task PersistAsync(
        Dictionary<string, Dictionary<string, ConsentRow>> doc, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, doc, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();

    /// <summary>On-disk row shape. The state is stored as its name so an unknown value fails loudly on read.</summary>
    private sealed record ConsentRow(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("purpose")] string Purpose,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
        [property: JsonPropertyName("effectiveFrom")] DateTimeOffset? EffectiveFrom,
        [property: JsonPropertyName("effectiveUntil")] DateTimeOffset? EffectiveUntil,
        [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
        [property: JsonPropertyName("signatureConsentRecordId")] string? SignatureConsentRecordId)
    {
        public static ConsentRow From(TenantConsentRecord r) => new(
            r.Id, r.Subject.Value, r.Purpose, r.Scope.Value, r.State.ToString(),
            r.RequestedAt, r.EffectiveFrom, r.EffectiveUntil, r.RevokedAt, r.SignatureConsentRecordId);

        public TenantConsentRecord ToRecord(TenantId tenant) => new()
        {
            Id = Id,
            Tenant = tenant,
            Subject = new SubjectId(Subject),
            Purpose = Purpose,
            Scope = ScopeExpression.Parse(Scope),
            State = Enum.Parse<ConsentLifecycleState>(State, ignoreCase: false),
            RequestedAt = RequestedAt,
            EffectiveFrom = EffectiveFrom,
            EffectiveUntil = EffectiveUntil,
            RevokedAt = RevokedAt,
            SignatureConsentRecordId = SignatureConsentRecordId,
        };
    }
}
