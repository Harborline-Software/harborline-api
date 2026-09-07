using System.Reflection;

using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 272 slice 5 — the acts that pass through a separation-of-duty decision are ENUMERATED, and each
/// one records that decision on its audit entry.
/// <para>
/// Ticket 222's acceptance is that no approval field is null on an act that passed through an approval or
/// duty decision. Slice 1's fence keeps a writer from DERIVING a fact; this one keeps the other failure
/// mode out: a second act family calling <see cref="SeparationOfDutyEngine.Decide"/> and dropping the
/// decision on the floor, so the entry it writes carries five nulls while a duty decision really was made.
/// Every production call site of the engine is discovered by SYMBOL (the same IL walk, over every shipped
/// <c>Harborline*.dll</c>) and must be an exact allow-list row naming the seam it records through.
/// </para>
/// </summary>
public sealed class SeparationOfDutyDecisionCallSiteFenceTests
{
    private const string DecideMethod =
        "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine.Decide";

    /// <summary>
    /// Every production caller of the engine today, as <c>file|symbol|callee</c>, with the audit seam the
    /// decision reaches. A row the scan stops finding fails <see cref="AllowListVacuityArchTests"/>.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["apps/local-node-host/Data/Workflow/NodeInvoiceApprovalCutover.cs"
            + "|Harborline.Api.LocalNodeHost.Data.Workflow.NodeInvoiceApprovalCutover.ResumeAsync("
            + "System.String,System.String,System.String,System.DateTimeOffset,"
            + "System.Threading.CancellationToken,"
            + "System.Nullable`1[Harborline.Api.Foundation.Authorization.AuthorizationWriteContext],"
            + "System.Nullable`1[Harborline.Api.LocalNodeHost.Data.Workflow.InvoiceApprovalOverride]"
            + "): System.Threading.Tasks.Task`1[Harborline.Api.LocalNodeHost.Data.Workflow"
            + ".InvoiceApprovalResumeResult]"
            + "|" + DecideMethod] =
            "the invoice-approval family: decides ONCE and records the decision through "
            + "AuthorizedAuditRecord.CopyFromDecision (IAuthorizedAuditTrail.AppendAuthorizedAsync with the "
            + "decision) BEFORE the act, on an approval and on a refusal alike",
    };

    /// <summary>Allow-list rows, for the vacuity registry.</summary>
    internal static string[] AllowedRows() => [.. Allowed.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredSeparationOfDutyDecisionCallSites() => Sites.Value;

    // Cached: the vacuity registry re-runs each registered discovery thirteen times across four threads.
    private static readonly Lazy<string[]> Sites = new(
        () => AuditAppendSymbolInventory
            .Discover(Classify)
            .Select(site => $"{site.File}|{site.Symbol}|{site.Kind}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A call to the one decider. Only the engine's own <c>Decide</c> counts as taking a decision.</summary>
    internal static string? Classify(MethodBase method) =>
        method.DeclaringType == typeof(SeparationOfDutyEngine) && method.Name == nameof(SeparationOfDutyEngine.Decide)
            ? DecideMethod
            : null;

    [Fact(DisplayName = "272 s5: every act family that takes a separation-of-duty decision is enumerated and records it")]
    public void Every_decision_call_site_is_an_allow_listed_recording_writer()
    {
        var offenders = Sites.Value.Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "A writer takes a separation-of-duty decision that no allow-list row accounts for. Ticket 222's "
            + "acceptance is that an act which passed through a duty decision records that decision's five "
            + "approval facts — add the writer here with the audit seam it records through, or route it "
            + "through one. Offending sites:\n  " + string.Join("\n  ", offenders));
    }

    [Fact(DisplayName = "272 s5: the decision-call-site scan actually finds the invoice approval writer")]
    public void The_scan_finds_the_invoice_approval_writer()
    {
        // Anti-vacuity: if the IL walk stopped resolving call tokens the assertion above would pass over
        // an empty set, and a second unrecorded decider could land unseen.
        Assert.Contains(
            Sites.Value,
            site => site.Contains("NodeInvoiceApprovalCutover.ResumeAsync", StringComparison.Ordinal));
        Assert.Equal(Allowed.Count, Sites.Value.Length);
    }

    [Fact(DisplayName = "272 s5: a planted second decider is rejected")]
    public void A_planted_offender_is_rejected()
    {
        // The mutation the fence exists for, run in-test rather than committed, so it runs every build.
        const string planted =
            "apps/local-node-host/Data/Identity/AdminTeamAccessAuthority.cs"
            + "|Harborline.Api.LocalNodeHost.Data.Identity.AdminTeamAccessAuthority.GrantAsync(): System.Void"
            + "|" + DecideMethod;

        var offenders = Sites.Value.Append(planted).Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.Equal([planted], offenders);
    }
}
