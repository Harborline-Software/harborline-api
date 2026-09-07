using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OrgBranding;

/// <summary>
/// Tenant-branding slice T1 — the fail-closed logo validator. Proves the gate: a valid raster (PNG / JPEG /
/// WebP) within the caps is accepted with its sniffed format + intrinsic dimensions; an oversized,
/// disallowed-type, over-dimension, or empty asset is REJECTED with a stable reason.
/// </summary>
public sealed class LogoAssetValidatorTests
{
    // ── accepted rasters (each format's header parsed WITHOUT a pixel decode) ──────────────────────────────

    [Fact(DisplayName = "PNG within caps is accepted with parsed dimensions")]
    public void Png_Accepted()
    {
        var r = LogoAssetValidator.Validate(Png(512, 256));
        Assert.True(r.Ok, r.RejectionReason);
        Assert.Equal("png", r.Format);
        Assert.Equal(512, r.Width);
        Assert.Equal(256, r.Height);
    }

    [Fact(DisplayName = "JPEG within caps is accepted with parsed dimensions")]
    public void Jpeg_Accepted()
    {
        var r = LogoAssetValidator.Validate(Jpeg(300, 400));
        Assert.True(r.Ok, r.RejectionReason);
        Assert.Equal("jpeg", r.Format);
        Assert.Equal(300, r.Width);
        Assert.Equal(400, r.Height);
    }

    [Fact(DisplayName = "WebP (VP8X) within caps is accepted with parsed dimensions")]
    public void Webp_Accepted()
    {
        var r = LogoAssetValidator.Validate(WebpVp8x(640, 128));
        Assert.True(r.Ok, r.RejectionReason);
        Assert.Equal("webp", r.Format);
        Assert.Equal(640, r.Width);
        Assert.Equal(128, r.Height);
    }

    // ── rejections ────────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "a disallowed type (GIF) is rejected as unsupported")]
    public void Gif_Rejected_UnsupportedType()
    {
        // "GIF89a" header — a valid image, but not a v1-accepted raster (raster-only PNG/WebP/JPEG).
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0x00, 0x10, 0x00 };
        var r = LogoAssetValidator.Validate(gif);
        Assert.False(r.Ok);
        Assert.Equal("unsupported-type", r.RejectionReason);
    }

    [Fact(DisplayName = "an SVG is rejected as unsupported (the XSS/DoS vector kept off the keystone)")]
    public void Svg_Rejected_UnsupportedType()
    {
        var svg = System.Text.Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        var r = LogoAssetValidator.Validate(svg);
        Assert.False(r.Ok);
        Assert.Equal("unsupported-type", r.RejectionReason);
    }

    [Fact(DisplayName = "an oversized asset is rejected before anything else")]
    public void Oversized_Rejected()
    {
        var big = Png(64, 64);
        Array.Resize(ref big, OrgBrandingDefaults.MaxLogoBytes + 1); // valid header, padded past the cap
        var r = LogoAssetValidator.Validate(big);
        Assert.False(r.Ok);
        Assert.Equal("exceeds-max-size", r.RejectionReason);
    }

    [Fact(DisplayName = "an over-dimension raster is rejected")]
    public void OverDimension_Rejected()
    {
        var r = LogoAssetValidator.Validate(Png(2000, 300)); // long edge > 1024
        Assert.False(r.Ok);
        Assert.Equal("exceeds-max-dimensions", r.RejectionReason);
    }

    [Fact(DisplayName = "an empty upload is rejected")]
    public void Empty_Rejected()
    {
        var r = LogoAssetValidator.Validate(ReadOnlySpan<byte>.Empty);
        Assert.False(r.Ok);
        Assert.Equal("empty-file", r.RejectionReason);
    }

    // ── minimal header fixtures (only the bytes the header-parsers read need be real) ──────────────────────

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        // 8-byte PNG signature.
        byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Array.Copy(sig, b, 8);
        // IHDR chunk: length (13, BE) + "IHDR" + width (BE) + height (BE).
        b[8] = 0x00; b[9] = 0x00; b[10] = 0x00; b[11] = 0x0D;
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        WriteBe32(b, 16, w);
        WriteBe32(b, 20, h);
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        // SOI (FF D8) then a SOF0 (FF C0) segment carrying precision + height + width + component count.
        var b = new byte[20];
        b[0] = 0xFF; b[1] = 0xD8;             // SOI
        b[2] = 0xFF; b[3] = 0xC0;             // SOF0
        b[4] = 0x00; b[5] = 0x11;             // segment length (17)
        b[6] = 0x08;                          // sample precision
        WriteBe16(b, 7, h);                   // height
        WriteBe16(b, 9, w);                   // width
        b[11] = 0x03;                         // component count
        return b;
    }

    private static byte[] WebpVp8x(int w, int h)
    {
        var b = new byte[30];
        // RIFF container.
        b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
        b[8] = (byte)'W'; b[9] = (byte)'E'; b[10] = (byte)'B'; b[11] = (byte)'P';
        // VP8X extended chunk.
        b[12] = (byte)'V'; b[13] = (byte)'P'; b[14] = (byte)'8'; b[15] = (byte)'X';
        // canvas (width-1) then (height-1), each 24-bit little-endian at offsets 24 and 27.
        WriteLe24(b, 24, w - 1);
        WriteLe24(b, 27, h - 1);
        return b;
    }

    private static void WriteBe16(byte[] b, int offset, int value)
    {
        b[offset] = (byte)((value >> 8) & 0xFF);
        b[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteBe32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)((value >> 24) & 0xFF);
        b[offset + 1] = (byte)((value >> 16) & 0xFF);
        b[offset + 2] = (byte)((value >> 8) & 0xFF);
        b[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteLe24(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)((value >> 8) & 0xFF);
        b[offset + 2] = (byte)((value >> 16) & 0xFF);
    }
}
