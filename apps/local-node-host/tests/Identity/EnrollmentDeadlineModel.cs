namespace Harborline.Api.LocalNodeHost.Tests.Identity;

internal static class EnrollmentDeadlineModel
{
    private const long MinimumMilliseconds = 15_000;

    internal static TimeSpan ForExchange(long fleetBootStartedAtMilliseconds, long exchangeStartedAtMilliseconds)
    {
        var elapsedMilliseconds = Math.Max(0, exchangeStartedAtMilliseconds - fleetBootStartedAtMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(MinimumMilliseconds, checked(4 * elapsedMilliseconds)));
    }
}
