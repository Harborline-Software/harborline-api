using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Packs.Install.Merge;

/// <summary>
/// The version a NARROWED pack form or workflow is published at (ticket 208 L624, slice 4b).
/// </summary>
/// <remarks>
/// <para>
/// A published form/workflow lives at an immutable <c>(tenant, id, version)</c> tuple: the projector's
/// <c>MatchesPinnedPackDefinition</c> refuses any content difference at a pinned tuple, and neither
/// lifecycle has a "replace the content at this version" operation. That is the same boundary the
/// ORDINARY authoring route has, and the ordinary route's answer is the one taken here: re-saving a
/// definition mints the NEXT version and publishes the new body on a fresh immutable tuple
/// (<c>FormDefinitionRoutes.MintNextVersionAsync</c>). A tenant narrowing is the pack-side spelling of
/// that mint — so the narrowed body is published at a DERIVED version and the unnarrowed tuple is
/// retracted in the same pass, rather than the pinned tuple being rewritten under its readers.
/// </para>
/// <para>
/// The derived version is a pure function of the seed version and the override, so a replay projects the
/// SAME tuple (an exact no-op) and the same override always yields the same version, on any node, in any
/// process — the tag is a SHA-256 fold of the patch's JSON, never a runtime-seeded hash.
/// </para>
/// <para>
/// <c>SemanticVersion</c> is three non-negative integers with no pre-release or build-metadata
/// segment (SemVer §9/§10 are deliberately not honoured on this substrate), so the tag has to live in the
/// patch segment: <c>major.minor.((patch + 1) * 1_000_000 + tag)</c>. Multiplying lifts every derived
/// version clear of the authoring range — no pack or authoring route will ever mint a patch segment of a
/// million — while <c>patch + 1</c> keeps derived versions of different seed versions in separate blocks
/// and ordered above the seed they narrow, so the catalogue's "highest published revision" read
/// (<c>GetCurrentPublishedAsync</c>, the read the surfaces use) resolves to the narrowed body. Readers
/// discover the tuple through that read; nothing outside this type does arithmetic on a version string.
/// Because the lifted patch can outrank later seed patches, the projector must withdraw every stale
/// published tuple at the admitted address, including those projected by prior versions of that pack.
/// </para>
/// </remarks>
public static class PackNarrowedVersion
{
    /// <summary>The size of the tag space folded out of the override's digest.</summary>
    private const int TagSpace = 1_000_000;

    /// <summary>The largest seed patch segment that can carry a tag without overflowing the segment.</summary>
    private const int MaximumTaggablePatch = (int.MaxValue / TagSpace) - 1;

    /// <summary>
    /// The version the narrowed body of <paramref name="seedVersion"/> publishes at under
    /// <paramref name="overlayPatch"/>. Deterministic: equal patches yield equal versions.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="seedVersion"/> is not <c>major.minor.patch</c>,
    /// or its patch segment is too large to carry a tag.</exception>
    public static string Derive(string seedVersion, JsonNode overlayPatch)
    {
        ArgumentNullException.ThrowIfNull(seedVersion);
        ArgumentNullException.ThrowIfNull(overlayPatch);

        var segments = seedVersion.Split('.');
        if (segments.Length != 3
            || !int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            throw new FormatException(
                $"A narrowed pack item version must be 'major.minor.patch'; got '{seedVersion}'.");
        }

        if (patch > MaximumTaggablePatch)
        {
            throw new FormatException(
                $"Seed version '{seedVersion}' cannot carry a narrowing tag: its patch segment exceeds "
                + $"{MaximumTaggablePatch}.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(overlayPatch.ToJsonString()));
        var tag = (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % TagSpace);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{major}.{minor}.{((patch + 1) * TagSpace) + tag}");
    }
}
