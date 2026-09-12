using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Published workflow-admission refusal codes.</summary>
public static class PackWorkflowAdmissionCodes
{
    public const string UnparseableWorkflow = "pack.install.admission.unparseable_workflow";
}

/// <summary>
/// The host ADAPTER that binds the Pack Composer install engine's <see cref="IPackContentAdmission"/> port
/// (foundation tier) to the REAL ADR 0143 workflow admission validator (<see cref="IWorkflowAdmissionValidator"/>,
/// in blocks-workflow). Fold A7 / S-9: install-time admission runs the SAME validator the authoring path
/// uses — no pack-scoped subset — over the COMPOSED post-install cascade, with classification RE-DERIVED
/// from the node's own ADR 0128 capability→authority registry (the validator the node registered carries
/// that registry; the pack author's labels are non-authoritative).
/// </summary>
/// <remarks>
/// This adapter is the only place that references BOTH the foundation-tier port and blocks-workflow — the
/// same seam-at-the-host split as <see cref="KernelAuditEnrollmentCompensatingControlRecorder"/>. A composed WorkflowDefinition item is
/// mapped through the canonical <see cref="WorkflowDefinitionWireMapper"/> (the SAME mapper the authoring
/// route uses) and validated; a mapper failure is itself a fail-closed refusal (a definition we cannot even
/// parse is inadmissible). Non-workflow content is not gated here.
/// </remarks>
public sealed class PackWorkflowAdmissionAdapter : IPackContentAdmission
{
    /// <summary>The refusal code for a workflow whose composed JSON cannot be mapped to the model.</summary>
    public const string UnparseableCode = PackWorkflowAdmissionCodes.UnparseableWorkflow;

    private readonly IWorkflowAdmissionValidator _admission;
    private readonly PackRestrictingDefinitionAdmission _restricting;
    private readonly IRoleGateAdmission? _roleGateAdmission;

    /// <summary>Constructs the adapter over the node's registered admission validator (registry-derived).</summary>
    public PackWorkflowAdmissionAdapter(
        IWorkflowAdmissionValidator admission,
        IRestrictingDefinitionKindValidator? kinds = null,
        IRoleGateAdmission? roleGateAdmission = null)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _restricting = new PackRestrictingDefinitionAdmission(
            kinds ?? RestrictingDefinitionKindValidator.Shared);
        _roleGateAdmission = roleGateAdmission;
    }

    /// <inheritdoc />
    public PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var refusals = _restricting.Validate(composed).ToList();
        refusals.AddRange(PackNavigationContentAdmission.Validate(composed, tenant, _roleGateAdmission));

        foreach (var item in composed.Where(c => c.Kind == PackContentKind.WorkflowDefinition))
        {
            WorkflowDefinition model;
            try
            {
                using var doc = JsonDocument.Parse(item.CanonicalJson);
                model = WorkflowDefinitionWireMapper.ToModel(
                    doc.RootElement, tenant.ToString(), item.Key, item.Version, CascadeLayer.Pack);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
            {
                refusals.Add(new PackAdmissionRefusal(
                    item.Key, PackWorkflowAdmissionCodes.UnparseableWorkflow, $"workflow could not be parsed: {ex.Message}"));
                continue;
            }

            var result = _admission.Validate(model);
            foreach (var violation in result.Violations)
            {
                refusals.Add(new PackAdmissionRefusal(item.Key, violation.Code, violation.Message));
            }
        }

        return refusals.Count == 0 ? PackAdmissionResult.Admissible : new PackAdmissionResult(refusals);
    }
}
