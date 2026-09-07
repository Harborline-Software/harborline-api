using System.Runtime.InteropServices;

namespace Harborline.Api.LocalNodeHost;

internal static partial class LocalNodeProcessFailure
{
    internal const int FatalExitCode = 70;

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;
    private static readonly object ErrorGate = new();

    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetErrorMode(
                SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception
                ?? new InvalidOperationException(
                    $"An unhandled non-Exception object escaped: {eventArgs.ExceptionObject}");
            _ = ReportFatal(exception, Console.Error);
            Environment.Exit(FatalExitCode);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            _ = ReportFatal(eventArgs.Exception, Console.Error);
            eventArgs.SetObserved();
        };
    }

    internal static int ReportFatal(Exception exception, TextWriter error)
    {
        try
        {
            lock (ErrorGate)
            {
                error.WriteLine(
                    $"local-node.fatal: {exception.GetType().Name}: {exception.Message}");
                error.WriteLine(exception.StackTrace);
                error.Flush();
            }
        }
        catch
        {
            // The fatal exit must not be defeated by a broken stderr stream.
        }

        return FatalExitCode;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint errorMode);
}
