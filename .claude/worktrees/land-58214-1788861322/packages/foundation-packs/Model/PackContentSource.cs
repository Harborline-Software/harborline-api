using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// A compose-time INPUT: one declarative definition the composer wants to package, supplied as a
/// parsed JSON document (<see cref="Content"/>). B-1a canonicalizes it (via
/// <c>Harborline.Api.Foundation.Crypto.CanonicalJson</c> — S-14 single source) and content-addresses it
/// into a <see cref="PackContentItem"/> for the manifest and pack file.
/// </summary>
/// <remarks>
/// The content is OPAQUE to B-1a — it is any declarative artifact (a form/workflow/catalog/config
/// definition). B-1a does not need the concrete .NET definition types; it canonicalizes, hashes, and
/// runs the value-level PII floor over the JSON. Concrete-type resolution is the composer's (B-2) and
/// the install engine's (B-1b) job.
/// </remarks>
/// <param name="Key">The stable content key of the item within the pack (S-10 upgrade-stable).</param>
/// <param name="Kind">The declarative kind of the item.</param>
/// <param name="Version">The PINNED version of the item.</param>
/// <param name="Content">The item's JSON body (parsed). Never captured instance data (S-6/S-12).</param>
public sealed record PackContentSource(
    string Key,
    PackContentKind Kind,
    string Version,
    JsonNode Content);
