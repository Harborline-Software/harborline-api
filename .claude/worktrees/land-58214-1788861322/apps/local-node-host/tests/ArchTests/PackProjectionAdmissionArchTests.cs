using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class PackProjectionAdmissionArchTests
{
    [Fact(DisplayName = "pack admission writes are installer-internal, never public store API")]
    public void PublicPackInstallStore_HasNoProjectionAdmissionWriter()
    {
        var publicMembers = typeof(IPackInstallStore).GetMethods().Select(method => method.Name).ToArray();

        Assert.DoesNotContain("RecordProjectionAdmission", publicMembers);
        Assert.DoesNotContain("ActivateAndRecordProjectionAdmission", publicMembers);
        Assert.DoesNotContain("DeactivateAndRecordProjectionAdmission", publicMembers);
        Assert.True(typeof(IPackProjectionAdmissionStore).IsPublic);
        Assert.DoesNotContain(typeof(IPackProjectionAdmissionStore),
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().Select(item => item.ServiceType));
        Assert.Empty(typeof(PackProjectionAuthority).GetConstructors());
    }
}
