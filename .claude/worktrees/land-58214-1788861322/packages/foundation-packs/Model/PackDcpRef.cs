using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Dcp;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// A manifest reference to the pack's Domain Compliance Profile — the DCP's own MERKLE LEAF (ADR 0145
/// D3.4). It lives on <see cref="PackManifest.Dcp"/> (a DISTINCT field, NOT inside
/// <see cref="PackManifest.Contents"/>), so the DCP is a separately-attributable governance artifact — the
/// solo→multi RACI fan-out (0145 D2) becomes a later permission change, not a re-architecture — while the
/// pack's content leaves (and their round-trip acceptance) stay untouched.
/// </summary>
/// <remarks>
/// Because this ref lives inside the signed <c>PackSignatureSubject.Manifest</c>, signing the manifest
/// binds the <see cref="ContentAddress"/> (S-14 merkle binding): the verifier re-hashes the carried DCP
/// payload and matches it here, so a swapped DCP is caught exactly like a swapped content item. The
/// <see cref="RegulatoryClass"/> is a signed convenience mirror of the payload's authoritative value — a
/// tamperer cannot make them diverge because ANY payload change breaks the content-address match.
/// </remarks>
/// <param name="ContentAddress">The content-address (SHA-256 CID) of the DCP's canonical bytes.</param>
/// <param name="RegulatoryClass">The declared regulatory class — a signed surface mirror (authoritative
/// value lives in the DCP payload the address binds).</param>
/// <param name="DcpVersion">The DCP's own version (pack-locked today; ADR 0145 open-Q3 lean).</param>
public sealed record PackDcpRef(
    Cid ContentAddress,
    RegulatoryClass RegulatoryClass,
    string DcpVersion);
