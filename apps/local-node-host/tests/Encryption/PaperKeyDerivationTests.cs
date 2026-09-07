using Harborline.Api.Foundation.Recovery;

namespace Harborline.Api.LocalNodeHost.Tests.Encryption;

public sealed class PaperKeyDerivationTests
{
    [Fact]
    public void EmbeddedEnglishWordListEncodesAndDecodesTheZeroSeedVector()
    {
        var seed = new byte[PaperKeyDerivation.EntropyByteLength];
        var expected = string.Join(" ", Enumerable.Repeat("abandon", 23).Append("art"));

        Assert.Equal(expected, PaperKeyDerivation.ToMnemonic(seed));
        Assert.Equal(seed, PaperKeyDerivation.FromMnemonic(expected));
    }
}
