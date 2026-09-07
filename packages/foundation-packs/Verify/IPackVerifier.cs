using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Verify;

/// <summary>
/// Verifies a pack file BEFORE anything reads its content (design §2.4 / S-7). Full signature
/// verification over the complete canonical byte stream, merkle content-address check, and
/// epoch-aware trust resolution — returning a typed verdict. The manifest/content are exposed only
/// on <see cref="PackVerdict.Verified"/>.
/// </summary>
public interface IPackVerifier
{
    /// <summary>
    /// Verifies <paramref name="packFileBytes"/> against <paramref name="trustStore"/> and returns
    /// the typed verdict. Never throws for a bad/tampered/untrusted pack — every failure is a
    /// fail-closed verdict, not an exception.
    /// </summary>
    PackVerificationResult Verify(ReadOnlySpan<byte> packFileBytes, IPackTrustStore trustStore);
}
