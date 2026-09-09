using System.Collections.Concurrent;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.LocalFirst.Export;

/// <summary>
/// Builds versioned JSON portability packages from the registered
/// <see cref="IExportContributor"/> instances.
/// </summary>
public sealed class JsonPortabilityDataExportService : IDataExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<IExportContributor> _contributors;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, ExportStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _downloads = new();

    /// <summary>Creates a portability export service over the registered contributors.</summary>
    public JsonPortabilityDataExportService(
        IEnumerable<IExportContributor> contributors,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = contributors.ToArray();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async ValueTask<ExportHandle> StartExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var exportId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        var handle = new ExportHandle { ExportId = exportId, StartedAt = startedAt };
        _statuses[exportId] = new ExportStatus
        {
            ExportId = exportId,
            State = ExportState.Running,
        };

        try
        {
            if (!string.Equals(request.Format, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Unsupported portability export format '{request.Format}'.");
            }

            var contributorPackages = new List<ContributorPackage>(_contributors.Count);
            foreach (var contributor in _contributors)
            {
                var entries = await ReadEntriesAsync(contributor, request, cancellationToken).ConfigureAwait(false);
                contributorPackages.Add(new ContributorPackage(
                    contributor.StableKey,
                    entries.Select(static entry => new EntryPackage(
                        entry.Key,
                        Convert.ToBase64String(entry.Payload.Span))).ToArray(),
                    ExportManifestVerifier.BuildManifest(contributor.StableKey, entries)));
            }

            var package = new PortabilityPackage(
                "harborline.portability.v1",
                exportId,
                startedAt,
                contributorPackages);
            _downloads[exportId] = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            _statuses[exportId] = new ExportStatus
            {
                ExportId = exportId,
                State = ExportState.Completed,
                ProgressPercent = 100,
                CompletedAt = startedAt,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _statuses[exportId] = new ExportStatus
            {
                ExportId = exportId,
                State = ExportState.Failed,
                CompletedAt = startedAt,
                ErrorDetail = exception.Message,
            };
        }

        return handle;
    }

    /// <inheritdoc />
    public ValueTask<ExportStatus> GetStatusAsync(
        Guid exportId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_statuses.TryGetValue(exportId, out var status)
            ? status
            : throw new KeyNotFoundException($"Export '{exportId}' was not found."));
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenDownloadAsync(
        Guid exportId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_downloads.TryGetValue(exportId, out var bytes))
        {
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        if (_statuses.ContainsKey(exportId))
        {
            throw new InvalidOperationException($"Export '{exportId}' is not available for download.");
        }

        throw new KeyNotFoundException($"Export '{exportId}' was not found.");
    }

    private static async ValueTask<IReadOnlyList<ExportEntry>> ReadEntriesAsync(
        IExportContributor contributor,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, ExportEntry>(StringComparer.Ordinal);
        foreach (var prefix in BuildPrefixes(request))
        {
            await foreach (var entry in contributor.ReadAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (MatchesRequestedScopes(entry.Key, request) &&
                    (request.Tenant is not TenantSelection.AllAccessible || IsAccessibleTenantEntry(entry.Key)))
                {
                    entries[entry.Key] = entry;
                }
            }
        }

        return entries.Values.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> BuildPrefixes(ExportRequest request)
    {
        var tenantPrefixes = request.Tenant switch
        {
            TenantSelection.ForSingle single => [TenantPrefix(single.TenantId)],
            TenantSelection.ForMultiple multiple => multiple.TenantIds.Select(TenantPrefix).ToArray(),
            TenantSelection.AllAccessible => ["tenants/"],
            null => [string.Empty],
            _ => throw new InvalidOperationException("Unknown tenant selection."),
        };

        return tenantPrefixes;
    }

    private static string TenantPrefix(TenantId tenantId) =>
        $"tenants/{Uri.EscapeDataString(tenantId.Value)}/";

    private static bool MatchesRequestedScopes(string key, ExportRequest request)
    {
        if (request.IncludeScopes.Count == 0)
        {
            return true;
        }

        var relativeKey = request.Tenant switch
        {
            TenantSelection.ForSingle single => RelativeToPrefix(key, TenantPrefix(single.TenantId)),
            TenantSelection.ForMultiple multiple => multiple.TenantIds
                .Select(tenantId => RelativeToPrefix(key, TenantPrefix(tenantId)))
                .FirstOrDefault(static relative => relative is not null),
            TenantSelection.AllAccessible => RelativeToTenantBoundary(key),
            null => RelativeToTenantBoundary(key) ?? key,
            _ => null,
        };

        return relativeKey is not null && request.IncludeScopes.Any(scope =>
            relativeKey.StartsWith($"{Uri.EscapeDataString(scope)}/", StringComparison.Ordinal));
    }

    private static string? RelativeToPrefix(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : null;

    private static string? RelativeToTenantBoundary(string key)
    {
        const string tenantsPrefix = "tenants/";
        if (!key.StartsWith(tenantsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var boundary = key.IndexOf('/', tenantsPrefix.Length, StringComparison.Ordinal);
        return boundary < 0 ? null : key[(boundary + 1)..];
    }

    private static bool IsAccessibleTenantEntry(string key)
    {
        const string tenantsPrefix = "tenants/";
        var boundary = key.IndexOf('/', tenantsPrefix.Length, StringComparison.Ordinal);
        return key.StartsWith(tenantsPrefix, StringComparison.Ordinal) &&
            boundary > tenantsPrefix.Length &&
            !key.AsSpan(tenantsPrefix.Length, boundary - tenantsPrefix.Length).StartsWith("__");
    }

    private sealed record PortabilityPackage(
        string Schema,
        Guid ExportId,
        DateTimeOffset CreatedAt,
        IReadOnlyList<ContributorPackage> Contributors);

    private sealed record ContributorPackage(
        string Key,
        IReadOnlyList<EntryPackage> Entries,
        ExportManifest Manifest);

    private sealed record EntryPackage(string Key, string PayloadBase64);
}
