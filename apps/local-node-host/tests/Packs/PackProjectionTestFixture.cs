using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>Real form/template consumers for route fixtures whose pack payloads carry these kinds.</summary>
internal static class PackProjectionTestFixture
{
    internal static PackSeedProjector Create(
        IPackInstallStore store, IEntityTypeRegistry types,
        IPackPlatformCompatibility? platform = null, IPackContentEdgeIndexProvider? edgeIndex = null)
    {
        var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        return new PackSeedProjector(store, types, NullLogger<PackSeedProjector>.Instance,
            templates: new InMemoryDocumentTemplateRegistry(), forms: forms, edgeIndex: edgeIndex,
            schemas: new InMemorySchemaRegistry(TimeProvider.System),
            authorizedForms: TestAuthorization.FormLifecycle(forms, TestAuthorization.AllowGate(), TestAuthorization.RoleGate()),
            platform: platform, time: TimeProvider.System);
    }

    internal static object FormContent(string title) => new
    {
        overlay = new
        {
            title = Text(title), description = Text("Collect details."),
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new[] { new { id = "main", title = Text("Main"), fields = new[] { "name" } } },
            rules = Array.Empty<object>(),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
        },
    };

    private static object Text(string value) => new
    {
        defaultLocale = "en",
        values = new Dictionary<string, string> { ["en"] = value },
    };
}
