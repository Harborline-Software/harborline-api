using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;

namespace Harborline.Api.Foundation.Packs.Serialization;

/// <summary>
/// Canonicalizes a <see cref="DomainComplianceProfile"/> into content-addressed canonical bytes — the
/// DCP's own merkle leaf (ADR 0145 D3.4). Delegates to <see cref="CanonicalJson"/> (the S-14 SINGLE
/// canonicalizer already used for content items + the signing subject — never a second canonicalizer) and
/// content-addresses with <see cref="Cid"/>. The resulting address is bound into the SIGNED manifest (via
/// <c>PackDcpRef</c>), so signing the pack merkle-binds the DCP and a swapped DCP payload is caught at
/// verify exactly like a swapped content item.
/// </summary>
public sealed class PackDcpCanonicalizer
{
    /// <summary>Canonicalizes <paramref name="dcp"/> to byte-stable canonical bytes + its content address.
    /// Fails closed on ill-formed UTF-16 (CanonicalJson rejects an unpaired surrogate rather than signing
    /// mangled bytes).</summary>
    public PackDcpItem Canonicalize(DomainComplianceProfile dcp)
    {
        ArgumentNullException.ThrowIfNull(dcp);

        // CanonicalJson.Serialize sorts keys + emits JCS-minimal escaping over the record's runtime
        // shape (enums as their numeric ordinal — deterministic), byte-identical across runs.
        var canonicalBytes = CanonicalJson.Serialize(dcp);
        var address = Cid.FromBytes(canonicalBytes);

        return new PackDcpItem(dcp, canonicalBytes, address);
    }
}

/// <summary>
/// A canonicalized, content-addressed DCP — the output of the <see cref="PackDcpCanonicalizer"/>. Carries
/// the exact canonical bytes hashed (<see cref="CanonicalBytes"/>) + the resulting <see cref="ContentAddress"/>,
/// mirroring <c>PackContentItem</c> for the DCP's dedicated governance leaf.
/// </summary>
/// <param name="Dcp">The profile these bytes canonicalize.</param>
/// <param name="CanonicalBytes">The exact canonical UTF-8 bytes signed-over (via the manifest address).</param>
/// <param name="ContentAddress">The content-address (SHA-256 CID) of <see cref="CanonicalBytes"/>.</param>
public sealed record PackDcpItem(
    DomainComplianceProfile Dcp,
    ReadOnlyMemory<byte> CanonicalBytes,
    Cid ContentAddress);
