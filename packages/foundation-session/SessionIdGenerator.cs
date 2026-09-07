using System;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// Mints opaque session ids from a CSPRNG (ADR 0099 S4): N random bytes from
/// <see cref="RandomNumberGenerator"/>, base64url-encoded (URL-safe alphabet, no padding) so
/// the value is safe in a cookie and carries no delimiter-collision risk. NOT hex (which
/// doubles length for no entropy gain).
/// </summary>
/// <remarks>
/// The entropy floor is <see cref="SessionOptions.MinimumSessionIdByteLength"/> (16 bytes /
/// 128-bit); <see cref="SessionOptions.SessionIdByteLength"/> defaults to 32 (256-bit). The
/// id is the cookie's bearer value AND the store key, so it is the only "session material" —
/// no claims, tenant, or roles are ever encoded into it (A6).
/// </remarks>
internal static class SessionIdGenerator
{
    /// <summary>
    /// Generates a fresh opaque session id with <paramref name="byteLength"/> bytes of CSPRNG
    /// entropy, base64url-encoded.
    /// </summary>
    /// <param name="byteLength">
    /// The number of random bytes. Validated by <see cref="SessionOptions.Validate"/> at
    /// registration to be ≥ <see cref="SessionOptions.MinimumSessionIdByteLength"/>; this method
    /// hard-floors it defensively in case it is called with a smaller value.
    /// </param>
    /// <returns>A URL-safe, unpadded base64url session id.</returns>
    public static string Generate(int byteLength)
    {
        if (byteLength < SessionOptions.MinimumSessionIdByteLength)
        {
            byteLength = SessionOptions.MinimumSessionIdByteLength;
        }

        Span<byte> buffer = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(buffer);

        // Base64Url (RFC 4648 §5): URL-safe alphabet, no padding. Available on net11.0 BCL.
        return Base64Url.EncodeToString(buffer);
    }
}
