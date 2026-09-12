using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class EnrollmentDeadlineModelTests
{
    [Fact]
    public void ForExchange_SamplesElapsedTimeWhenTheExchangeBegins()
    {
        const long fleetBootStartedAt = 1_000;

        Assert.Equal(
            TimeSpan.FromSeconds(15),
            EnrollmentDeadlineModel.ForExchange(fleetBootStartedAt, fleetBootStartedAt + 3_750));
        Assert.Equal(
            TimeSpan.FromSeconds(24),
            EnrollmentDeadlineModel.ForExchange(fleetBootStartedAt, fleetBootStartedAt + 6_000));
    }
}
