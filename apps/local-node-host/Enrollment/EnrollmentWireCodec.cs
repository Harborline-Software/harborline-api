using System;
using System.Text.Json;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// Serializes the two-sided wire-enrollment <see cref="EnrollmentRequest"/> / <see cref="EnrollmentResponse"/>
/// to/from the OPAQUE byte payload kernel-sync carries in its <c>ENROLL_REQUEST</c> / <c>ENROLL_RESPONSE</c>
/// frames (#1301 F-1). This is the host's job: the enrollment protocol lives ABOVE kernel-sync, so the host owns
/// turning the protocol records into the bytes the transport ships, and decoding the bytes the transport delivers.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON, reusing the proven HTTP shape.</b> The HTTP transport already round-trips the FULL
/// <see cref="EnrollmentResponse"/> graph (anchor, admissions with <c>AdmissionSignature</c>, the
/// transport-key map) via System.Text.Json (<c>AdmissionRoutes</c> returns it with <c>Results.Ok</c>;
/// <c>HttpEnrollmentTransport</c> deserializes it with <c>ReadFromJsonAsync&lt;EnrollmentResponse&gt;</c>). So
/// JSON serialization of these records is established + tested. We reuse it here for the socket payload rather
/// than introduce a second CBOR codec for the same records — one serialization story, no drift.
/// </para>
/// <para>
/// <b>Fail-closed decode.</b> A malformed / truncated payload throws <see cref="JsonException"/>; every caller
/// catches it and treats it as a fail-closed reject (the admitter rejects an unparseable request; the joiner
/// adopts nothing on an unparseable response).
/// </para>
/// </remarks>
public static class EnrollmentWireCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Encode the joiner's request to the opaque payload bytes.</summary>
    public static byte[] EncodeRequest(EnrollmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.SerializeToUtf8Bytes(request, Options);
    }

    /// <summary>Decode the joiner's request from opaque payload bytes. Throws <see cref="JsonException"/> /
    /// <see cref="ArgumentException"/> on malformed input (caller treats as fail-closed reject).</summary>
    public static EnrollmentRequest DecodeRequest(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new JsonException("Empty enrollment request payload.");
        }
        return JsonSerializer.Deserialize<EnrollmentRequest>(payload, Options)
            ?? throw new JsonException("Enrollment request payload decoded to null.");
    }

    /// <summary>Encode A's bootstrap response to the opaque payload bytes.</summary>
    public static byte[] EncodeResponse(EnrollmentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return JsonSerializer.SerializeToUtf8Bytes(response, Options);
    }

    /// <summary>Decode A's bootstrap response from opaque payload bytes. Throws <see cref="JsonException"/> on
    /// malformed input (caller treats as fail-closed — adopt nothing).</summary>
    public static EnrollmentResponse DecodeResponse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new JsonException("Empty enrollment response payload.");
        }
        return JsonSerializer.Deserialize<EnrollmentResponse>(payload, Options)
            ?? throw new JsonException("Enrollment response payload decoded to null.");
    }
}
