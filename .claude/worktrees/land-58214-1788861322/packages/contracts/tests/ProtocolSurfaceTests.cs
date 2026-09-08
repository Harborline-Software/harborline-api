using Harborline.Api.Protocol;

namespace Harborline.Api.Protocol.Tests;

/// <summary>
/// Pins the generated C# constant surface to the codegen manifest. The TypeScript lane pins the
/// same surface against literals; asserting against the manifest instead means an edited schema
/// that was never regenerated fails here, in a lane CI actually runs.
/// </summary>
public sealed class ProtocolSurfaceTests
{
    [Fact]
    public void MirrorsTheProtocolIdentityDeclaredByTheManifest()
    {
        // Bound to locals so the xunit analyzer does not insist the generated constant is the
        // "expected" value: the manifest is the authority and the generated constant is under test.
        var generatedId = HarborlineProtocol.Id;
        var generatedVersion = HarborlineProtocol.Version;

        Assert.Equal((string?)ProtocolFixtures.ProtocolManifest["protocolId"], generatedId);
        Assert.Equal((string?)ProtocolFixtures.ProtocolManifest["protocolVersion"], generatedVersion);
    }

    [Fact]
    public void MirrorsTheCompleteHostCommandSurface()
    {
        var declared = ProtocolFixtures.Operations("HarborlineHostPort")
            .Select(operation => (string)operation["hostCommand"]!)
            .OrderBy(command => command, StringComparer.Ordinal);

        var generated = ProtocolFixtures.ConstantsOf(typeof(HarborlineHostCommands))
            .OrderBy(command => command, StringComparer.Ordinal);

        // Two empty sequences compare equal, so a resolution failure on either side would pass
        // silently. The manifest declares ten host commands and must never declare none.
        Assert.NotEmpty(declared);
        Assert.Equal(declared, generated);
    }

    [Fact]
    public void MirrorsTheCompleteApplicationRouteSurface()
    {
        var declared = ProtocolFixtures.Operations("HarborlineApplicationPort")
            .Select(operation => (string)operation["path"]!)
            .OrderBy(path => path, StringComparer.Ordinal);

        var generated = ProtocolFixtures.ConstantsOf(typeof(HarborlineApplicationRoutes))
            .OrderBy(path => path, StringComparer.Ordinal);

        Assert.NotEmpty(declared);
        Assert.Equal(declared, generated);
    }
}
