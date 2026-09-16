using System.Text.Json;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Health.WebSession;

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
        [AdminTeamAccessRoutes.RevokeGrantRequest.Id] = AdminTeamAccessRoutes.RevokeGrantRequest,
        [AdminTeamAccessRoutes.NarrowScopeRequest.Id] = AdminTeamAccessRoutes.NarrowScopeRequest,
        [AdminTeamAccessRoutes.ReviewGrantRequest.Id] = AdminTeamAccessRoutes.ReviewGrantRequest,
        [AccessHoldersRead.SelectedReadRequest.Id] = AccessHoldersRead.SelectedReadRequest,
        [SelectedFormSubmitRoutes.SubmitRequest.Id] = SelectedFormSubmitRoutes.SubmitRequest,
        [SelectedPackReplacementRoutes.ReplaceRequest.Id] = SelectedPackReplacementRoutes.ReplaceRequest,
    };

    internal static CompiledViewRequest Resolve(JsonElement dispatch) =>
        ViewRequestBindingAdmission.Resolve(dispatch, Requests);

    internal static CompiledViewRequest Admit(JsonElement dispatch, ViewRequestBindingSources sources) =>
        ViewRequestBindingAdmission.Admit(dispatch, Requests, sources);

    internal static CompiledViewRequest ResolveDataSource(JsonElement dispatch) =>
        RequireListSource(Resolve(dispatch));

    internal static CompiledViewRequest AdmitDataSource(JsonElement dispatch, ViewRequestBindingSources sources) =>
        RequireListSource(Admit(dispatch, sources));

    private static CompiledViewRequest RequireListSource(CompiledViewRequest request)
    {
        // Data sources run on view load, without the explicit intention required by actions.
        if (request.Descriptor.Method != "GET" || request.Descriptor.RowsPointer is null
            || request.Descriptor.RowIdentityPointer is null)
            throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
        return request;
    }
}
