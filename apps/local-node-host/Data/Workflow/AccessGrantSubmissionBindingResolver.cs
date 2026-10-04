using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>Pins the Access workflow declared by the admitted active pack snapshot at submission.</summary>
internal sealed class AccessGrantSubmissionBindingResolver(
    IPackInstallStore packs, IWorkflowDefinitionExecutionStore definitions) : IFormSubmissionBindingResolver
{
    public async ValueTask<DefinitionCoordinates?> ResolveAsync(
        FormDefinition definition, DateTimeOffset submittedAt, CancellationToken ct = default)
    {
        if (definition.Id.Value != "access.grant-a-role") return null;
        if (definition.PackSource is not { } source)
            throw new InvalidOperationException("Access submission requires authoritative pack provenance.");
        var active = packs.GetActive(definition.Tenant, source.PackId)
            ?? throw new InvalidOperationException("The submitted Access form has no active owning pack.");
        // An unchanged form tuple may retain an older PackSource version. The active immutable seed
        // snapshot must contain this exact form tuple; its workflow is the submission's selected pair.
        var form = active.SeedItems.SingleOrDefault(item => item.Kind == PackContentKind.FormDefinition
            && item.Key == definition.Id.Value && item.Version == definition.Version.ToString());
        var workflow = active.SeedItems.SingleOrDefault(item => item.Kind == PackContentKind.WorkflowDefinition
            && item.Key == GrantIssuanceSteps.DefinitionKey);
        if (form is null || workflow is null)
            throw new InvalidOperationException("The active pack does not contain the submitted Access form/workflow pair.");
        using var authored = JsonDocument.Parse(workflow.CanonicalJson);
        if (!authored.RootElement.TryGetProperty("subjectFormRef", out var subject)
            || !subject.TryGetProperty("formId", out var formId) || formId.GetString() != definition.Id.Value
            || !subject.TryGetProperty("version", out var version) || version.GetString() != definition.Version.ToString())
            throw new InvalidOperationException("The active pack workflow does not declare the submitted Access form revision.");
        var coordinate = new DefinitionCoordinates(definition.Tenant, workflow.Key, workflow.Version);
        // Admission remains the execution store's responsibility; no lenient catalog read authorizes execution.
        _ = await definitions.GetAdmittedAsync(coordinate, ct).ConfigureAwait(false);
        return coordinate;
    }
}
