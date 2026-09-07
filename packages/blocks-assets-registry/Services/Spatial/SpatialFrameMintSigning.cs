using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The canonical, signable payload a spatial-frame mint signature covers (ADR 0168 D2-A4(c)) —
/// produced through the same <c>IOperationSigner</c> / <c>CanonicalJson</c> /
/// <c>SignedOperation&lt;T&gt;</c> path the trust roster and the home-epoch fence use.
/// </summary>
/// <remarks>
/// <para>Bindings that carry the invariants: <see cref="PreviousEpoch"/> (no replay against a
/// different predecessor), <see cref="GrantingHomeEpoch"/> (a device demoted at home-epoch N cannot
/// keep producing verifiable mints), and <see cref="ContentHash"/> (a triple-only signature attests
/// that epoch N was minted, and nothing about what the frame IS).</para>
/// <para><see cref="PayloadType"/> is the payload-type domain separator
/// (<c>spatial-frame-mint/v1</c>): the signable envelope has no type tag and these keys sign other
/// payload types; the constant field cannot be added retroactively.</para>
/// <para>Canonical forms are pinned, and normalization precedes signing: <see cref="FrameCode"/> is
/// the [A14]-normalized form, <see cref="Anchor"/> the <c>RegistryEntityId</c> canonical string,
/// <see cref="TenantId"/> its canonical string form.</para>
/// </remarks>
public sealed record SpatialFrameMintSignaturePayload(
    string TenantId,
    string Anchor,
    string FrameCode,
    long FrameEpoch,
    long PreviousEpoch,
    string HomeDeviceId,
    long GrantingHomeEpoch,
    string ContentHash)
{
    /// <summary>The constant payload-type domain separator, INSIDE the signed payload.</summary>
    public string PayloadType { get; } = "spatial-frame-mint/v1";
}

/// <summary>
/// The pinned <c>contentHash</c> construction (ADR 0168 D2-A4(c)): lowercase-hex SHA-256 over
/// <c>CanonicalJson.Serialize</c> of a named canonical record of the four defining fields, in which
/// every ordinate and quaternion component serializes as a decimal string (shortest round-trip,
/// invariant culture) and absent optional members are OMITTED entirely — <c>null</c> is not a
/// permitted value in the canonical record. The canonicalizer's cross-language byte-identity covers
/// the integer/string/bool domain only; this record keeps IEEE-754 doubles out of the signed bytes.
/// </summary>
public static class SpatialFrameContentHashing
{
    /// <summary>Computes the canonical content hash over the four defining fields.</summary>
    public static string Compute(
        string axisConvention,
        string originDescription,
        string lengthUnit,
        SpatialFrameGeoreference? georeference)
    {
        var canonical = new ContentRecord(
            AxisConvention: axisConvention,
            OriginDescription: originDescription,
            LengthUnit: lengthUnit,
            Georeference: georeference is null
                ? null
                : new GeoreferenceRecord(
                    ObservedAt: georeference.ObservedAt,
                    GeodeticCrs: georeference.GeodeticCrs,
                    OriginPosition: georeference.OriginPosition.Select(Decimalize).ToArray(),
                    Orientation: georeference.Orientation?.Select(Decimalize).ToArray(),
                    PoseBasis: georeference.PoseBasis));

        var bytes = CanonicalJson.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Shortest round-trip decimal string, invariant culture (the pinned ordinate form).</summary>
    private static string Decimalize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException(
                "Georeference ordinates must be finite — NaN/Infinity have no canonical decimal form.");
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The NAMED canonical content record — the hash preimage shape, pinned.</summary>
    private sealed record ContentRecord(
        string AxisConvention,
        string OriginDescription,
        string LengthUnit,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GeoreferenceRecord? Georeference);

    /// <summary>The canonical georeference sub-record (ordinates as decimal strings).</summary>
    private sealed record GeoreferenceRecord(
        string ObservedAt,
        string GeodeticCrs,
        IReadOnlyList<string> OriginPosition,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Orientation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PoseBasis);
}
