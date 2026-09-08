using Harborline.Api.LocalNodeHost;

LocalNodeProcessFailure.Initialize();

try
{
    await LocalNodeHostComposition.RunAsync(args);
    return Environment.ExitCode;
}
catch (Exception exception)
{
    return LocalNodeProcessFailure.ReportFatal(exception, Console.Error);
}
