namespace Harborline.Api.LocalNodeHost.Tests;

public sealed class EntrypointFatalTests
{
    [Fact]
    public void ReportFatal_Formats_Stderr_And_Returns_ExSoftware()
    {
        var error = new StringWriter();
        Exception exception;
        try
        {
            throw new InvalidOperationException("composition failed");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        var exitCode = LocalNodeProcessFailure.ReportFatal(exception, error);
        var lines = error.ToString().Split(Environment.NewLine);

        Assert.Equal(70, exitCode);
        Assert.Equal(
            "local-node.fatal: InvalidOperationException: composition failed",
            lines[0]);
        Assert.Contains(nameof(ReportFatal_Formats_Stderr_And_Returns_ExSoftware), error.ToString());
    }
}
