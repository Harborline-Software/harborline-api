using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The v1 workflow DEFINITIONS the node host registers (ADR 0135 slice 2) — single source of truth so the
/// composition root, the route that instantiates an approval Process, and the arch-tests reference the SAME
/// decision-table versions (no drift).
/// </summary>
public static class NodeWorkflowDefinitions
{
    /// <summary>The pinned version string for the v1 invoice-approval threshold table (D7).</summary>
    public const string InvoiceApprovalV1Version = "2026-06-23.1";

    /// <summary>The v1 threshold — invoices STRICTLY ABOVE this require human approval (the CP gate).</summary>
    public const decimal V1ApprovalThreshold = 5000m;


    /// <summary>
    /// Ticket 272 slice 3 — the ONE <see cref="ApprovalThreshold"/> the invoice-approval family decides
    /// under, built once here from <see cref="V1ApprovalThreshold"/> and never re-read at a point of use.
    /// The route's response, <see cref="NodeInvoiceApprovalCutover.ShouldRouteToEngine"/> and the
    /// separation-of-duty request all take the number from THIS object, so the gate boundary, the audited
    /// applied limit and the pinned decision table cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <b>One approver required.</b> ADR 0135's v1 invoice-approval Process parks on a single approve
    /// human-task, so exactly one approver is on record for an approve; a higher count here would refuse
    /// every approval the Process can produce. The limit source is the pinned decision table itself —
    /// a definition-borne signing limit, not a per-principal grant.
    /// </remarks>
    public static readonly ApprovalThreshold InvoiceApprovalThreshold = new(
        Policy: new ApprovalPolicyFact(InvoiceApprovalSteps.DefinitionKey, InvoiceApprovalV1Version),
        Limit: V1ApprovalThreshold,
        RequiredApprovers: 1,
        LimitSource: new ApprovalLimitSourceFact(
            ApprovalLimitSourceKind.SigningLimitAgreement,
            InvoiceApprovalSteps.DefinitionKey,
            InvoiceApprovalV1Version));

    /// <summary>
    /// The v1 invoice-approval threshold decision table (D5/D7): one effective-dated version pinning the
    /// <c>&gt; $5k → require approval</c> rule. An instance pins <see cref="InvoiceApprovalV1Version"/> at
    /// instantiation; a future threshold change adds a NEW effective-dated version without altering pinned
    /// instances. The "over" row floors just above $5k so $5000.00 exactly auto-approves (strictly-above gate).
    /// </summary>
    public static ThresholdDecisionTable InvoiceApprovalThresholdTable() => new(new[]
    {
        new ThresholdDecisionTableVersion
        {
            Version = InvoiceApprovalV1Version,
            EffectiveFrom = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero),
            Rows = new[]
            {
                new ThresholdDecisionRow("under-5k", 0m, ApprovalDecision.AutoApprove),
                new ThresholdDecisionRow("over-5k", V1ApprovalThreshold + 0.01m, ApprovalDecision.RequireApproval),
            },
        },
    });
}
