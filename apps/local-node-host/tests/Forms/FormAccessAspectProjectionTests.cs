using System.Text.Json;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Health;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed class FormAccessAspectProjectionTests
{
    [Fact]
    public void Aspect_overlay_projects_nonempty_access_roles_to_json()
    {
        var json = JsonSerializer.SerializeToElement(AspectOverlayDto.From(new AspectOverlay(
            Access: new AccessAspect(ReadRoles: ["overlay-read-opaque"], WriteRoles: ["overlay-write-opaque"]))));

        var access = json.GetProperty("access");
        Assert.Equal("""["overlay-read-opaque"]""", access.GetProperty("readRoles").GetRawText());
        Assert.Equal("""["overlay-write-opaque"]""", access.GetProperty("writeRoles").GetRawText());
    }

    [Fact]
    public void Access_gate_projects_nonempty_aspect_roles_to_json()
    {
        var json = JsonSerializer.SerializeToElement(AccessGateDto.From(new AccessAspect(
            ReadRoles: ["gate-read-opaque"], WriteRoles: ["gate-write-opaque"])));

        Assert.Equal("""["gate-read-opaque"]""", json.GetProperty("readRoles").GetRawText());
        Assert.Equal("""["gate-write-opaque"]""", json.GetProperty("writeRoles").GetRawText());
    }
}
