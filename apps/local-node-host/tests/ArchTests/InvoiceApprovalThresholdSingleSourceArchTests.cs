using System;
using System.Globalization;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ADR 0135 invoice-approval threshold — the #1357 deep-review's <b>Finding 1 (threshold single-source)</b>
/// fast-follow as an ARCH-TEST.
/// </summary>
/// <remarks>
/// <para>
/// The issue-route gate (<see cref="NodeInvoiceApprovalCutover.ShouldRouteToEngine"/> → route to the engine
/// iff <c>Total &gt; V1ApprovalThreshold</c>, strictly above) and the pinned decision table's "over" row
/// (<c>RequireApproval</c> iff <c>amount &gt;= over-row floor</c>) encode the SAME boundary in two places.
/// They already DERIVE from the single <see cref="NodeWorkflowDefinitions.V1ApprovalThreshold"/> constant
/// (the route reads it directly; the over-row floor is <c>V1ApprovalThreshold + 0.01m</c>) — so this is the
/// arch-test that PINS that agreement, so a future edit to either side cannot silently open the gap interval
/// between "the route routes to the engine" and "the table requires approval".
/// </para>
/// <para>
/// <b>The invariant (the gap interval must stay empty):</b> for every representable money amount, the route's
/// route-to-engine decision MUST equal the pinned table's require-approval decision. If they ever disagree at
/// any boundary cent, an over-threshold invoice could be sent to the engine yet auto-approve in the table
/// (split-brain) — or the reverse. The boundary cents are the only place they can diverge, so the test pins
/// every cent across the boundary plus the endpoints.
/// </para>
/// <para>
/// <b>Proof the fence bites:</b> bump the over-row floor in <see cref="NodeWorkflowDefinitions"/> away from
/// <c>V1ApprovalThreshold + 0.01m</c> (e.g. to <c>+ 1.00m</c>), or change the route gate to <c>&gt;=</c>, and
/// <see cref="RouteGate_And_PinnedTable_AgreeAtEveryBoundaryCent"/> fails immediately — the empty gap interval
/// has opened.
/// </para>
/// </remarks>
public sealed class InvoiceApprovalThresholdSingleSourceArchTests
{
    private static readonly string PinnedVersion = NodeWorkflowDefinitions.InvoiceApprovalV1Version;
    private static readonly decimal Threshold = NodeWorkflowDefinitions.V1ApprovalThreshold;

    /// <summary>
    /// The route gate and the pinned decision table agree at every cent across the boundary (and at the
    /// endpoints): "route to engine" ⇔ "require approval", for all of them. This is the single-source
    /// guarantee — the gap interval between the two encodings is provably empty.
    /// </summary>
    [Fact(DisplayName = "Finding 1: the issue-route gate (>$5k) and the pinned over-row (require-approval) AGREE at every boundary cent — the gap interval is empty")]
    public void RouteGate_And_PinnedTable_AgreeAtEveryBoundaryCent()
    {
        var table = NodeWorkflowDefinitions.InvoiceApprovalThresholdTable();

        // Walk every cent in a window straddling the boundary: well below → well above. This covers the only
        // amounts where the two encodings could diverge (the boundary cents), plus normal-magnitude amounts.
        for (var cents = (long)((Threshold - 5m) * 100m); cents <= (long)((Threshold + 5m) * 100m); cents++)
        {
            var amount = cents / 100m;

            var routeToEngine = NodeInvoiceApprovalCutover.ShouldRouteToEngine(InvoiceWithTotal(amount));
            var requiresApproval =
                table.EvaluatePinned(PinnedVersion, amount).Decision == ApprovalDecision.RequireApproval;

            Assert.True(
                routeToEngine == requiresApproval,
                $"Threshold split-brain at amount {amount.ToString(CultureInfo.InvariantCulture)}: " +
                $"route routes-to-engine={routeToEngine} but pinned table requires-approval={requiresApproval}. " +
                "The issue-route gate and the decision-table over-row boundary have diverged (the gap interval " +
                "between them is no longer empty). Keep both derived from NodeWorkflowDefinitions.V1ApprovalThreshold.");
        }
    }

    /// <summary>
    /// The endpoints pin the strictly-above semantics directly: $5000.00 exactly is DIRECT (auto-approve);
    /// the very next cent ($5000.01) is the first amount that requires approval.
    /// </summary>
    [Fact(DisplayName = "Finding 1: $5000.00 exactly is direct (auto-approve) on BOTH the route and the table; $5000.01 is the first require-approval amount on BOTH")]
    public void StrictlyAboveSemantics_AreSharedByBothEncodings()
    {
        var table = NodeWorkflowDefinitions.InvoiceApprovalThresholdTable();

        // Exactly at the threshold → DIRECT on both.
        Assert.False(NodeInvoiceApprovalCutover.ShouldRouteToEngine(InvoiceWithTotal(Threshold)));
        Assert.Equal(ApprovalDecision.AutoApprove, table.EvaluatePinned(PinnedVersion, Threshold).Decision);

        // The very next representable cent → REQUIRE APPROVAL on both.
        var firstOver = Threshold + 0.01m;
        Assert.True(NodeInvoiceApprovalCutover.ShouldRouteToEngine(InvoiceWithTotal(firstOver)));
        Assert.Equal(ApprovalDecision.RequireApproval, table.EvaluatePinned(PinnedVersion, firstOver).Decision);
    }

    // ── Helper: a minimal Draft invoice whose Total is the amount under test ──────
    // Mirrors InvoiceRoutes' own Invoice.Create / InvoiceLine.Create construction. A single line at
    // qty 1 × unitPrice `total` (no tax) → Invoice.Total == total — the only field ShouldRouteToEngine reads.
    private static Invoice InvoiceWithTotal(decimal total)
    {
        var invoiceId = new InvoiceId(Guid.NewGuid().ToString("N"));
        var line = InvoiceLine.Create(
            invoiceId:       invoiceId,
            lineNumber:      1,
            description:     "Consulting",
            quantity:        1m,
            unitPrice:       total,
            incomeAccountId: new GLAccountId("4000"));
        return Invoice.Create(
            tenantId:      new TenantId("arch-test"),
            chartId:       new ChartOfAccountsId("CH-1"),
            invoiceNumber: "INV-2026-03-01-AA-0001",
            customerId:    new PartyId("customer-1"),
            issueDate:     new DateOnly(2026, 3, 1),
            dueDate:       new DateOnly(2026, 3, 31),
            lines:         new[] { line },
            arAccountId:   new GLAccountId("1100"),
            id:            invoiceId,
            createdAtUtc:  new Instant(System.TimeProvider.System.GetUtcNow()));
    }
}
