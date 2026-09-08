namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>The outcome of validating an uploaded logo asset (<see cref="LogoAssetValidator.Validate"/>).</summary>
/// <param name="Ok">True when the asset is an accepted raster within the size/dimension caps.</param>
/// <param name="Format">The sniffed format (<c>png</c> / <c>webp</c> / <c>jpeg</c>) when recognized; else null.</param>
/// <param name="Width">The decoded pixel width (0 when unread).</param>
/// <param name="Height">The decoded pixel height (0 when unread).</param>
/// <param name="RejectionReason">A stable machine-code reason when rejected (a UI/i18n key seed); else null.</param>
public sealed record LogoValidationResult(
    bool Ok, string? Format, int Width, int Height, string? RejectionReason);

/// <summary>
/// Pure, fail-closed validator for an uploaded tenant-logo asset (tenant-branding design §2.2, slice T1).
/// Content-sniffs the bytes (never trusts a client-declared content-type), enforces raster-only formats plus
/// the size and dimension caps, and reads the intrinsic dimensions from the file header WITHOUT decoding the
/// pixels (no image library dependency).
/// </summary>
/// <remarks>
/// <para>
/// <b>Raster-only, sniff-based (CIC ruling / design §8 Q2).</b> Only PNG, WebP, and JPEG magic-byte
/// signatures are accepted; an SVG / GIF / BMP / anything-else is rejected as <c>unsupported-type</c>. This
/// keeps an SVG XSS/DoS sanitizer off the v1 keystone. Validation is fail-closed: an oversized /
/// disallowed-type / unreadable asset is rejected at upload with a plain reason — never stored-then-hidden.
/// </para>
/// <para>
/// <b>Dimensions from the header.</b> Width/height are parsed from the format's header block (PNG IHDR, JPEG
/// SOFn marker, WebP VP8/VP8L/VP8X chunk). This is intentionally a validate-and-reject posture, not a
/// downscale — a real image codec (server-side downscale) is a follow-up; rejecting an oversized asset is the
/// safe, dependency-free v1 floor and matches the "rejected at upload" gate.
/// </para>
/// </remarks>
public static class LogoAssetValidator
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Validate the uploaded bytes. Returns an accepted result with the sniffed format + dimensions,
    /// or a rejection with a stable reason code.</summary>
    public static LogoValidationResult Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return Reject("empty-file");
        }

        if (bytes.Length > OrgBrandingDefaults.MaxLogoBytes)
        {
            return Reject("exceeds-max-size"); // > 512 KB
        }

        // Sniff by magic bytes — the client-declared content-type is never trusted.
        if (IsPng(bytes))
        {
            return FromDimensions("png", TryReadPngDimensions(bytes));
        }
        if (IsJpeg(bytes))
        {
            return FromDimensions("jpeg", TryReadJpegDimensions(bytes));
        }
        if (IsWebp(bytes))
        {
            return FromDimensions("webp", TryReadWebpDimensions(bytes));
        }

        return Reject("unsupported-type"); // not PNG/WebP/JPEG (e.g. SVG, GIF, BMP)
    }

    private static LogoValidationResult FromDimensions(string format, (int W, int H)? dims)
    {
        if (dims is not { } d || d.W <= 0 || d.H <= 0)
        {
            return new LogoValidationResult(false, format, 0, 0, "unreadable-dimensions");
        }

        if (Math.Max(d.W, d.H) > OrgBrandingDefaults.MaxLogoLongEdgePx)
        {
            return new LogoValidationResult(false, format, d.W, d.H, "exceeds-max-dimensions"); // > 1024 px
        }

        return new LogoValidationResult(true, format, d.W, d.H, null);
    }

    private static LogoValidationResult Reject(string reason) =>
        new(false, null, 0, 0, reason);

    // ── format sniffing ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 24 && b[..8].SequenceEqual(PngSignature);

    private static bool IsJpeg(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> b) =>
        b.Length >= 30
        && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    // ── dimension readers (header-only, no pixel decode) ────────────────────────────────────────────────

    /// <summary>PNG: IHDR width/height are big-endian 4-byte ints at offsets 16 and 20.</summary>
    private static (int, int)? TryReadPngDimensions(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24)
        {
            return null;
        }

        var w = ReadBe32(b, 16);
        var h = ReadBe32(b, 20);
        return (w, h);
    }

    /// <summary>JPEG: walk the marker segments to the first Start-Of-Frame (SOFn) and read its 2-byte
    /// big-endian height then width.</summary>
    private static (int, int)? TryReadJpegDimensions(ReadOnlySpan<byte> b)
    {
        var pos = 2; // skip the SOI (FF D8)
        while (pos + 9 < b.Length)
        {
            if (b[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            var marker = b[pos + 1];

            // Standalone markers (no length payload): padding / TEM / RSTn / SOI / EOI.
            if (marker == 0xFF || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                pos += 2;
                continue;
            }

            var segLen = ReadBe16(b, pos + 2);
            if (segLen < 2)
            {
                return null;
            }

            // SOF0..SOF15, excluding the non-frame markers C4 (DHT), C8 (JPG), CC (DAC).
            if (marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (pos + 9 >= b.Length)
                {
                    return null;
                }

                var h = ReadBe16(b, pos + 5);
                var w = ReadBe16(b, pos + 7);
                return (w, h);
            }

            pos += 2 + segLen;
        }

        return null;
    }

    /// <summary>WebP: the first chunk after the RIFF/WEBP header carries the canvas dimensions — one of VP8
    /// (lossy), VP8L (lossless), or VP8X (extended).</summary>
    private static (int, int)? TryReadWebpDimensions(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30)
        {
            return null;
        }

        // fourcc of the first chunk at offset 12.
        var fourcc = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4));
        switch (fourcc)
        {
            case "VP8 ":
                // Lossy: 14-bit width/height (little-endian) at offsets 26 and 28.
                var lw = (b[26] | (b[27] << 8)) & 0x3FFF;
                var lh = (b[28] | (b[29] << 8)) & 0x3FFF;
                return (lw, lh);

            case "VP8L":
                // Lossless: after the 0x2F signature byte at 20, 14-bit (width-1) then 14-bit (height-1).
                if (b[20] != 0x2F)
                {
                    return null;
                }
                var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                var wl = (bits & 0x3FFF) + 1;
                var hl = ((bits >> 14) & 0x3FFF) + 1;
                return (wl, hl);

            case "VP8X":
                // Extended: 24-bit (canvas width-1) then 24-bit (canvas height-1), little-endian, at 24/27.
                var wx = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                var hx = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                return (wx, hx);

            default:
                return null;
        }
    }

    private static int ReadBe16(ReadOnlySpan<byte> b, int offset) =>
        (b[offset] << 8) | b[offset + 1];

    private static int ReadBe32(ReadOnlySpan<byte> b, int offset) =>
        (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
}
