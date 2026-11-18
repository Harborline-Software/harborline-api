using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// The platform release bundle for the seed, as the release workflow stages it (T-705 D4 and D5):
/// the export, its <c>SHA256SUMS</c> and the release receipt. A null member is a missing file.
/// </summary>
internal sealed record PlatformSeedBundle(byte[]? Export, byte[]? Sha256Sums, byte[]? ReleaseReceipt);

/// <summary>
/// The recorded published referent (ADR-0102 decision 2): the published platform seed a node may sign
/// install material over. No production referent exists until the owner authorizes the platform release
/// (T-670/published-digest-evidence), so a node installs no seed until then (ADR-0102 decision 4).
/// </summary>
internal sealed record PlatformSeedReferent(string Revision, string ExportSha256, string ReleaseReceiptSha256);

/// <summary>What the node records for an installed seed (T-670/api-seed-intake acceptance line 1).</summary>
internal sealed record PlatformSeedInstallRecord(
    string Revision, string ExportSha256, string Sha256SumsLine, string ReleaseReceipt, string LocalSigner);

/// <summary>A verified bundle: only this is handed to conversion.</summary>
internal sealed record VerifiedPlatformSeed(
    byte[] Export, string Revision, string ExportSha256, string Sha256SumsLine, string ReleaseReceipt);

/// <summary>A bundle refused before any item is read, converted, signed or installed.</summary>
internal sealed class PlatformSeedRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Verifies the platform seed bundle before the node reads any item of it.</summary>
internal static class PlatformSeedIntake
{
    internal const string ExportFile = "platform-pack.export.json";
    internal const string SumsFile = "SHA256SUMS";
    internal const string ReceiptFile = "release-receipt.json";

    /// <summary>
    /// Checks every file is present, the export and receipt hashes against <c>SHA256SUMS</c>, the export's
    /// declared digest against the recomputed one and the receipt against the export, then requires a
    /// recorded published referent naming exactly this export and receipt.
    /// </summary>
    internal static VerifiedPlatformSeed Verify(PlatformSeedBundle bundle, PlatformSeedReferent? referent)
    {
        var export = bundle.Export ?? throw Refuse("platform-seed-export-missing", $"The bundle carries no {ExportFile}.");
        var sums = bundle.Sha256Sums ?? throw Refuse("platform-seed-sums-missing", $"The bundle carries no {SumsFile}.");
        var receipt = bundle.ReleaseReceipt ?? throw Refuse("platform-seed-receipt-missing", $"The bundle carries no {ReceiptFile}.");

        var listed = ParseSums(sums);
        var exportSha256 = Sha256(export);
        if (!listed.TryGetValue(ExportFile, out var listedExport) || listedExport != exportSha256)
            throw Refuse("platform-seed-export-sums-mismatch", $"The {ExportFile} hash differs from its {SumsFile} line.");
        var receiptSha256 = Sha256(receipt);
        if (!listed.TryGetValue(ReceiptFile, out var listedReceipt) || listedReceipt != receiptSha256)
            throw Refuse("platform-seed-receipt-sums-mismatch", $"The {ReceiptFile} hash differs from its {SumsFile} line.");

        var (revision, declared) = ReadIdentity(export);
        if (declared != RecomputeDeclaredDigest(export))
            throw Refuse("platform-seed-digest-mismatch", "The export's declared digest differs from the recomputed one.");
        var commit = ReadReceipt(receipt, exportSha256, revision, declared);

        if (referent is null)
            throw Refuse("platform-seed-referent-absent",
                "No published referent is recorded for the platform seed; the node does not sign or install it (ADR-0102 decision 4).");
        if (referent.Revision != revision || referent.ExportSha256 != exportSha256 || referent.ReleaseReceiptSha256 != receiptSha256)
            throw Refuse("platform-seed-referent-mismatch", "The bundle is not the recorded published referent.");

        return new VerifiedPlatformSeed(export, revision, exportSha256, $"{exportSha256}  {ExportFile}",
            $"harborline-platform@{commit} {ReceiptFile} sha256:{receiptSha256}");
    }

    /// <summary>
    /// Maps what the unchanged conversion cannot onto the pack the node installs (T-584 seed-released-navigation.md
    /// 58 and 59): the export's package-level <c>capabilityRequirements</c>, and each navigation body's
    /// <c>workspaceItem</c> and <c>entriesItem</c> pointers, resolved from the items they name to the api's
    /// <c>labelKey</c>, <c>itemIds</c> and <c>items</c>; and each view item's version, from its own body. A pointer or
    /// version that does not resolve is refused, never dropped.
    /// </summary>
    internal static PackExportRequest ResolveForInstall(PackExportRequest request, ReadOnlySpan<byte> export)
    {
        var root = JsonNode.Parse(export)!;
        var payloads = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in root["items"]!.AsArray())
            if (item?["id"] is JsonValue id && id.TryGetValue<string>(out var key))
                payloads.TryAdd(key, item["content"]?["payload"]);

        var requirements = root["capabilityRequirements"] switch
        {
            null => [],
            JsonArray array when array.All(entry => entry is JsonValue value && value.TryGetValue<string>(out _)) =>
                array.Select(entry => entry!.GetValue<string>()).ToArray(),
            _ => throw Refuse("platform-seed-capability-requirements-malformed",
                "The export's capabilityRequirements is not a list of capability names."),
        };
        var contents = request.Contents
            .Select(item => item.Kind == PackContentKind.NavWorkspaceConfig
                ? item with { Content = ResolveNavigation(item.Key, item.Content.DeepClone(), payloads) }
                : item.Kind == PackContentKind.ViewDefinition
                    ? item with { Version = BodyVersion(item.Key, item.Content) }
                    : item)
            .ToArray();
        return request with { Contents = contents, CapabilityRequirements = requirements };
    }

    private static JsonNode ResolveNavigation(string key, JsonNode body, Dictionary<string, JsonNode?> payloads)
    {
        foreach (var workspace in body["seedWorkspaces"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (workspace.Remove("workspaceItem", out var workspaceItem))
                workspace["labelKey"] = Text(Pointed(key, workspaceItem, payloads)?["labelKey"], key);
            foreach (var group in workspace["groups"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (!group.Remove("entriesItem", out var entriesItem)) continue;
                if (Pointed(key, entriesItem, payloads)?["members"] is not JsonArray members)
                    throw Unresolved(key);
                var entries = members
                    .Select(member => (Id: Text(member?["id"], key), LabelKey: Text(member?["labelKey"], key),
                        Order: member?["order"] is JsonValue order && order.TryGetValue<int>(out var at) ? at : throw Unresolved(key)))
                    .OrderBy(entry => entry.Order)
                    .ToArray();
                group["itemIds"] = new JsonArray(entries.Select(entry => (JsonNode?)entry.Id).ToArray());
                group["items"] = new JsonArray(entries
                    .Select(entry => (JsonNode?)new JsonObject { ["id"] = entry.Id, ["labelKey"] = entry.LabelKey })
                    .ToArray());
            }
        }
        return body;
    }

    /// <summary>
    /// A view carries its own version in its body (DES-0017 views-ck-2 and views-ck-7); the seed revision is the
    /// package's. The body is the one place that states it (ADR-0102 decision 5), so the item takes its value.
    /// </summary>
    private static string BodyVersion(string key, JsonNode body) =>
        body["version"] is JsonValue value && value.TryGetValue<string>(out var version) && !string.IsNullOrWhiteSpace(version)
            ? version
            : throw Refuse("platform-seed-view-version-missing", $"The view body '{key}' carries no version of its own.");

    private static JsonNode? Pointed(string key, JsonNode? pointer, Dictionary<string, JsonNode?> payloads) =>
        pointer is JsonValue value && value.TryGetValue<string>(out var id) && payloads.TryGetValue(id, out var payload)
            ? payload
            : throw Unresolved(key);

    private static string Text(JsonNode? node, string key) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw Unresolved(key);

    private static PlatformSeedRefusedException Unresolved(string key) => Refuse("platform-seed-navigation-unresolved",
        $"A navigation pointer in '{key}' does not resolve to an export item carrying the api's shape.");

    private static Dictionary<string, string> ParseSums(byte[] sums)
    {
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(sums).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || !listed.TryAdd(line[(separator + 2)..], line[..separator]))
                throw Refuse("platform-seed-sums-malformed", $"{SumsFile} is not a list of '<sha256>  <file>' lines.");
        }
        return listed;
    }

    private static (string Revision, string Declared) ReadIdentity(byte[] export)
    {
        try
        {
            using var document = JsonDocument.Parse(export);
            var root = document.RootElement;
            return (root.GetProperty("revision").GetString()!, root.GetProperty("digest").GetProperty("value").GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Refuse("platform-seed-digest-mismatch", "The export declares no revision and digest.");
        }
    }

    /// <summary>The platform exporter's two-pass rule: SHA-256 of the document with its trailing digest member removed.</summary>
    private static string RecomputeDeclaredDigest(byte[] export)
    {
        var text = Encoding.UTF8.GetString(export);
        var marker = text.LastIndexOf(",\"digest\":{", StringComparison.Ordinal);
        return marker < 0 ? string.Empty : Sha256(Encoding.UTF8.GetBytes(text[..marker] + "}\n"));
    }

    private static string ReadReceipt(byte[] receipt, string exportSha256, string revision, string declared)
    {
        try
        {
            using var document = JsonDocument.Parse(receipt);
            var root = document.RootElement;
            var pack = root.GetProperty("pack");
            if (root.GetProperty("artifact").GetProperty("digest").GetString() == exportSha256
                && pack.GetProperty("version").GetString() == revision
                && pack.GetProperty("seedDigest").GetString() == declared)
                return root.GetProperty("commit").GetString()!;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw Refuse("platform-seed-receipt-mismatch", "The release receipt does not name this export.");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static PlatformSeedRefusedException Refuse(string code, string message) => new(code, message);
}
