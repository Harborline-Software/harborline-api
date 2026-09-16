using System.Text.Json;
using Harborline.Api.Foundation.ViewDefinitions;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The closed set of view transports implemented by this host. Each route owns its descriptor;
/// a pack can only bind its inputs. Additions require both a route and composition tests.
/// </summary>
internal static class HostViewRequestDescriptors
{
    private static readonly Dictionary<string, ViewRequestDescriptor> Requests = new(StringComparer.Ordinal)
    {
        [AssetRegistryRoutes.ReadEntityRequest.Id] = AssetRegistryRoutes.ReadEntityRequest,
        [AccessHoldersRead.ReadRequest.Id] = AccessHoldersRead.ReadRequest,
    };

    internal static CompiledViewRequest Resolve(JsonElement dispatch) =>
        ViewRequestBindingAdmission.Resolve(dispatch, Requests);

    internal static CompiledViewRequest Admit(JsonElement dispatch, ViewRequestBindingSources sources) =>
        ViewRequestBindingAdmission.Admit(dispatch, Requests, sources);
}
