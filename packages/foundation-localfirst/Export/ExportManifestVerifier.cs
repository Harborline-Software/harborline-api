using System.Security.Cryptography;

namespace Harborline.Api.Foundation.LocalFirst.Export;

/// <summary>Kind of mismatch <see cref="ExportManifestVerifier"/> found for one key.</summary>
public enum ExportVerificationKind
{
    /// <summary>The entry's recomputed digest matches the manifest.</summary>
    Intact = 0,

    /// <summary>The key is in the manifest but its recomputed digest does not match.</summary>
    Corrupted = 1,

    /// <summary>The key is in the manifest but no matching entry was supplied to verify.</summary>
    Missing = 2,

    /// <summary>The key was supplied to verify but is not present in the manifest.</summary>
    Extra = 3,
}

/// <summary>One verification finding for a single key.</summary>
public sealed record ExportVerificationResult(string Key, ExportVerificationKind Kind);

/// <summary>
/// Recomputes SHA-256 over each supplied entry's payload bytes and compares
/// it against an <see cref="ExportManifest"/> built earlier, reporting per-key
/// intact / corrupted / missing / extra findings.
///
/// <para>
/// <b>What this proves and what it does not:</b> a clean verification means
/// the supplied bytes are byte-for-byte identical, per key, to what the
/// manifest was built from — it detects corruption, deletion, and addition
/// relative to that manifest. It does <b>not</b> prove the manifest itself
/// is authentic or was produced by a trusted party: an attacker who controls
/// both the payload and the manifest can regenerate a "clean" pair. Proving
/// authenticity / anti-replacement requires a signed manifest over a trust
/// anchor (P2-blocked; see <see cref="ExportManifest"/>). Treat a PASS here
/// as "internally consistent," not as "authentic."
/// </para>
/// </summary>
public static class ExportManifestVerifier
{
    /// <summary>Computes a manifest over the given entries (does not read from any store).</summary>
    public static ExportManifest BuildManifest(string contributorKey, IReadOnlyList<ExportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(contributorKey);
        ArgumentNullException.ThrowIfNull(entries);

        var rows = new ExportManifestEntry[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            rows[i] = new ExportManifestEntry(entry.Key, entry.Payload.Length, ComputeSha256Hex(entry.Payload.Span));
        }

        return new ExportManifest(contributorKey, rows, rows.Length);
    }

    /// <summary>
    /// Verifies the given entries against a previously built manifest, returning
    /// one result per key seen in either the manifest or the supplied entries.
    /// </summary>
    public static IReadOnlyList<ExportVerificationResult> Verify(ExportManifest manifest, IReadOnlyList<ExportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entries);

        var manifestByKey = new Dictionary<string, ExportManifestEntry>(StringComparer.Ordinal);
        foreach (var row in manifest.Entries)
        {
            manifestByKey[row.Key] = row;
        }

        var results = new List<ExportVerificationResult>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            seenKeys.Add(entry.Key);

            if (!manifestByKey.TryGetValue(entry.Key, out var manifestRow))
            {
                results.Add(new ExportVerificationResult(entry.Key, ExportVerificationKind.Extra));
                continue;
            }

            var actualDigest = ComputeSha256Hex(entry.Payload.Span);
            var kind = string.Equals(actualDigest, manifestRow.Sha256Hex, StringComparison.Ordinal)
                && entry.Payload.Length == manifestRow.ByteLength
                ? ExportVerificationKind.Intact
                : ExportVerificationKind.Corrupted;
            results.Add(new ExportVerificationResult(entry.Key, kind));
        }

        foreach (var row in manifest.Entries)
        {
            if (!seenKeys.Contains(row.Key))
            {
                results.Add(new ExportVerificationResult(row.Key, ExportVerificationKind.Missing));
            }
        }

        return results;
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> payload)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, digest);
        return Convert.ToHexStringLower(digest);
    }
}
