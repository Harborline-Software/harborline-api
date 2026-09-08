namespace Harborline.Api.Foundation.LocalFirst.Export;

/// <summary>
/// One entry contributed to an export: a stable key and its raw payload bytes.
/// Format-agnostic, mirroring <see cref="IOfflineStore"/>'s byte-array contract.
/// </summary>
public sealed record ExportEntry(string Key, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Manifest row for one exported entry: its key, byte length, and a SHA-256
/// digest (lower-case hex) computed over the entry's payload bytes.
/// </summary>
public sealed record ExportManifestEntry(string Key, long ByteLength, string Sha256Hex);

/// <summary>
/// The full manifest for one contributor's export: which contributor produced
/// it, the per-entry digest rows, and the entry count.
///
/// <para>
/// <b>Scope of what this proves:</b> this manifest lets a verifier detect
/// <i>corruption</i> (bytes changed since the manifest was built), a
/// <i>missing</i> entry, or an <i>extra</i> entry not present at manifest
/// time. It does <b>not</b> prove authenticity or resist a wholesale
/// replacement of both the payload and the manifest together — that
/// requires a signed manifest over a trust anchor, which is a P2-blocked
/// follow-up (Ed25519 signing + key custody are out of scope here). Do not
/// present this manifest as proof the export is authentic or untampered by
/// its producer; it only proves internal consistency between payload and
/// manifest as given.
/// </para>
/// </summary>
public sealed record ExportManifest(string ContributorKey, IReadOnlyList<ExportManifestEntry> Entries, int Count);

/// <summary>
/// Contributes one module's worth of entries to an export, in stable order,
/// scoped by a caller-supplied scope string (e.g. an offline-store key
/// prefix). Contributors are read-only: they do not write, sign, or package
/// anything — that composition belongs to the (not-yet-built) export
/// service that plugs contributors together.
/// </summary>
public interface IExportContributor
{
    /// <summary>Stable identifier for this contributor, used as the manifest's owning key.</summary>
    string StableKey { get; }

    /// <summary>
    /// Streams this contributor's entries for the given scope, in a stable
    /// order (same scope + same underlying data always yields the same
    /// order and bytes).
    /// </summary>
    IAsyncEnumerable<ExportEntry> ReadAsync(string scope, CancellationToken cancellationToken = default);
}
