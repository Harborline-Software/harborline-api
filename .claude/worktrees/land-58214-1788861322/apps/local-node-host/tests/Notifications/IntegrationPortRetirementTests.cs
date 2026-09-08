using Harborline.Api.Foundation.Integrations;

namespace Harborline.Api.LocalNodeHost.Tests.Notifications;

public sealed class IntegrationPortRetirementTests
{
    [Fact]
    public void Public_surface_does_not_ship_the_unconsumed_email_port()
    {
        var exportedTypeNames = typeof(IMockVendorProvider).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(
            "Harborline.Api.Foundation.Integrations.Email.IEmailProvider",
            exportedTypeNames);
    }

    [Fact]
    public void Public_surface_does_not_ship_the_unimplemented_messaging_gateway()
    {
        var exportedTypeNames = typeof(IMockVendorProvider).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(
            "Harborline.Api.Foundation.Integrations.Messaging.IMessagingGateway",
            exportedTypeNames);
    }
}
