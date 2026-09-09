using System;
using System.Threading.Tasks;

namespace Harborline.Api.Quality.AnalyzerCanary;

// Every member is deliberately noncompliant. eng/verify-analyzer-canary.sh
// asserts the upstream IDs in SARIF, rather than accepting a generic red build.
internal static class Violations
{
    internal static int SynchronousWait() => Task.FromResult(1).Result;

    internal static async void AsyncVoid()
    {
        await Task.Yield();
    }

    internal static void EmptyGeneralCatch()
    {
        try
        {
            throw new InvalidOperationException();
        }
        catch (Exception)
        {
        }
    }
}
