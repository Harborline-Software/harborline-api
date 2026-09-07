using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Migration.Erpnext.Extraction;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Read-only ERPNext dump source-inventory preview. The route intentionally stops before every
/// load/write seam: it parses a machine-local SQL dump and returns content-free DocType names,
/// classifications, and source row counts only.
/// </summary>
public static class ErpnextImportPreviewRoutes
{
    /// <summary>The source-inventory preview route.</summary>
    public const string PreviewRoute = "/api/local-node/import/erpnext/preview";

    /// <summary>The operator-configured directory that may contain previewable dumps.</summary>
    public const string ImportRootConfigurationKey = "LocalNode:ErpnextImport:ImportRoot";

    /// <summary>Maps the read-only preview route onto the shared local-node listener.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(PreviewRoute, async (
            ErpnextImportPreviewRequest? request,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request?.DumpFilePath))
            {
                return Results.BadRequest(new ErpnextImportPreviewError("dump-path-required"));
            }

            var configuredRoot = configuration[ImportRootConfigurationKey];
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                return Results.Json(
                    new ErpnextImportPreviewError("dump-import-root-not-configured"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(request.DumpFilePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new ErpnextImportPreviewError("dump-path-invalid"));
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(configuredRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.Json(
                    new ErpnextImportPreviewError("dump-import-root-invalid"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!IsPathWithinRoot(fullPath, fullRoot))
            {
                return Results.BadRequest(new ErpnextImportPreviewError("dump-path-outside-import-root"));
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".sql", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErpnextImportPreviewError("dump-file-type-unsupported"));
            }

            if (!File.Exists(fullPath))
            {
                return Results.NotFound(new ErpnextImportPreviewError("dump-file-not-found"));
            }

            loggerFactory
                .CreateLogger("Harborline.Api.LocalNodeHost.ErpnextImportPreview")
                .LogInformation("Previewing ERPNext dump at {DumpFilePath}.", fullPath);

            try
            {
                var reader = await MariaDbDumpSourceReader.LoadAsync(fullPath, ct).ConfigureAwait(false);
                var inventory = await new MariaDbDumpExtractor(reader).ReadInventoryAsync(ct).ConfigureAwait(false);
                return Results.Ok(ToResponse(inventory));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(
                    new ErpnextImportPreviewError("dump-file-unreadable"),
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException)
            {
                return Results.UnprocessableEntity(new ErpnextImportPreviewError("dump-file-invalid"));
            }
        });
    }

    internal static bool IsPathWithinRoot(string fullPath, string fullRoot)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(fullRoot);
        var relativePath = Path.GetRelativePath(rootPath, fullPath);

        return !Path.IsPathRooted(relativePath) &&
               !relativePath.Equals("..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    internal static ErpnextImportPreviewResponse ToResponse(ErpnextSourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var entries = inventory.Entries
            .OrderBy(entry => entry.DocType, StringComparer.Ordinal)
            .Select(entry => new ErpnextImportPreviewEntry(
                entry.DocType,
                ToWireClassification(entry.Classification),
                entry.SourceRowCount))
            .ToArray();

        return new ErpnextImportPreviewResponse(
            PreviewOnly: true,
            SourceMode: inventory.SourceMode == SourceAccessMode.MariaDbDump ? "mariaDbDump" : "unknown",
            AccountedRowCount: entries.Sum(entry => (long)entry.SourceRowCount),
            MappedRowCount: entries
                .Where(entry => entry.Classification == "mapped")
                .Sum(entry => (long)entry.SourceRowCount),
            KnownIrrelevantRowCount: entries
                .Where(entry => entry.Classification == "knownIrrelevant")
                .Sum(entry => (long)entry.SourceRowCount),
            UnmappedRowCount: entries
                .Where(entry => entry.Classification == "unmapped")
                .Sum(entry => (long)entry.SourceRowCount),
            Entries: entries);
    }

    private static string ToWireClassification(ErpnextDocTypeClassification classification) => classification switch
    {
        ErpnextDocTypeClassification.Mapped => "mapped",
        ErpnextDocTypeClassification.KnownIrrelevant => "knownIrrelevant",
        ErpnextDocTypeClassification.UnmappedUnknown => "unmapped",
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null),
    };
}

/// <summary>Wire request for a machine-local ERPNext SQL dump preview.</summary>
public sealed record ErpnextImportPreviewRequest(string DumpFilePath);

/// <summary>One content-free DocType census row returned to Harborline App.</summary>
public sealed record ErpnextImportPreviewEntry(string DocType, string Classification, int SourceRowCount);

/// <summary>The complete accounted source inventory. <c>PreviewOnly</c> is always true.</summary>
public sealed record ErpnextImportPreviewResponse(
    bool PreviewOnly,
    string SourceMode,
    long AccountedRowCount,
    long MappedRowCount,
    long KnownIrrelevantRowCount,
    long UnmappedRowCount,
    IReadOnlyList<ErpnextImportPreviewEntry> Entries);

/// <summary>Stable machine-readable refusal returned without leaking a local filesystem path.</summary>
public sealed record ErpnextImportPreviewError(string Code);
