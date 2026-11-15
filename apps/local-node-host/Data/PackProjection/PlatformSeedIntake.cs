using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
