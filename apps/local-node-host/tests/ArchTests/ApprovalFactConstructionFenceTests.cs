using System.Reflection;

using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 272 slice 1 — the approval facts belong to the separation-of-duty engine, and to nothing else.
/// <para>
/// ADR 0067 clause 2 puts one decider behind the approval facts an audit entry records. The failure mode it
/// exists to prevent is a writer computing a fact for itself — reading an ambient threshold, inventing a
/// policy version, deciding a separation-of-duty outcome inline — because a fact derived at the writer is a
/// second decision, and the audit entry then misstates the authority the act was made under.
/// </para>
/// <para>
/// The inventory is discovered by SYMBOL: an IL walk over every shipped <c>Harborline*.dll</c> for
/// <c>newobj</c> of an approval-fact type, anchored to file:line through the portable PDB. Aliases, helper
/// locals and target-typed <c>new</c> do not hide a construction site from it. Every discovered site must be
/// the engine, the decision it returns, or an explicitly allowed row below.
/// </para>
/// </summary>
public sealed class ApprovalFactConstructionFenceTests
{
    /// <summary>The approval-fact types no writer may construct. Both spellings: the engine's and audit's.</summary>
    private static readonly string[] FactTypes =
    [
        "Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalPolicyFact",
        "Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalLimitSourceFact",
        "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyFact",
        "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision",
        "Harborline.Api.Kernel.Audit.AuthorityPolicySnapshot",
        "Harborline.Api.Kernel.Audit.AuthorityLimitSourceSnapshot",
        "Harborline.Api.Kernel.Audit.SeparationOfDutySnapshot",
    ];

    private const string EngineSymbol =
        "packages/foundation-authorization/SeparationOfDuty/SeparationOfDutyEngine.cs"
        + "|Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine.Decide"
        + "(Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyRequest)"
        + ": Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision|";

    private const string SnapshotCopySymbol =
        "packages/kernel-audit/AuthoritySnapshot.cs"
        + "|Harborline.Api.Kernel.Audit.AuthoritySnapshot.Copy(): Harborline.Api.Kernel.Audit.AuthoritySnapshot|";

    private const string SeamSymbol =
        "packages/kernel-audit/AuthorityCapturingAuditTrail.cs"
        + "|Harborline.Api.Kernel.Audit.AuthorizedAuditRecord.CopyFromDecision("
        + "Harborline.Api.Kernel.Audit.AuditRecord,"
        + "Harborline.Api.Foundation.Authorization.AuthorizationDecision,"
        + "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision"
        + "): Harborline.Api.Kernel.Audit.AuditRecord|";

    private const string InvoiceThresholdSymbol =
        "apps/local-node-host/Data/Workflow/NodeWorkflowDefinitions.cs"
        + "|Harborline.Api.LocalNodeHost.Data.Workflow.NodeWorkflowDefinitions..cctor(): System.Void|";

    /// <summary>
    /// The exact set of non-engine sites that construct an approval fact today. Each row is
    /// <c>file|symbol|constructed type</c> as the scan reports it, with the reason it is not an offender.
    /// A row here that the scan no longer finds fails <see cref="AllowListVacuityArchTests"/>.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        [EngineSymbol + "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyFact"] =
            "the engine itself — the one place the separation-of-duty fact is decided",
        [EngineSymbol + "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision"] =
            "the engine returning its own decision object",
        [SnapshotCopySymbol + "Harborline.Api.Kernel.Audit.AuthorityPolicySnapshot"] =
            "AuthoritySnapshot.Copy severs the persisted snapshot from the decision's own instances; it "
            + "copies the policy fact field-for-field and derives nothing (ADR 0068 clause 1)",
        [SnapshotCopySymbol + "Harborline.Api.Kernel.Audit.AuthorityLimitSourceSnapshot"] =
            "AuthoritySnapshot.Copy, field-for-field copy of the limit source; derives nothing",
        [SnapshotCopySymbol + "Harborline.Api.Kernel.Audit.SeparationOfDutySnapshot"] =
            "AuthoritySnapshot.Copy, field-for-field copy of the SoD verdict; derives nothing",
        [SeamSymbol + "Harborline.Api.Kernel.Audit.AuthorityPolicySnapshot"] =
            "ticket 272 slice 2: the one seam that maps a separation-of-duty decision onto the stored "
            + "snapshot; every field is read off the decision, nothing is derived",
        [SeamSymbol + "Harborline.Api.Kernel.Audit.AuthorityLimitSourceSnapshot"] =
            "the same seam, limit source; the kind is a default-arm-free switch over the decided kind",
        [InvoiceThresholdSymbol
            + "Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalPolicyFact"] =
            "ticket 272 slice 3: the ONE place the invoice-approval family's ApprovalThreshold is built, "
            + "from the single V1ApprovalThreshold constant. A threshold is an INPUT to the engine, not a "
            + "decided fact — the writers read this object and never a threshold at a point of use",
        [InvoiceThresholdSymbol
            + "Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalLimitSourceFact"] =
            "the same threshold, naming the pinned decision table it comes from; nothing is derived per act",
        [SeamSymbol + "Harborline.Api.Kernel.Audit.SeparationOfDutySnapshot"] =
            "the same seam, SoD verdict and refusal reason; both are default-arm-free switches over the "
            + "decided members, so a refusal cannot be stored as a pass",
    };

    /// <summary>Allow-list rows, for the vacuity registry.</summary>
    internal static string[] AllowedRows() => [.. Allowed.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredApprovalFactConstructionSites() => Discover();

    // Cached: the IL walk reads every shipped assembly, and the vacuity registry re-runs each registered
    // discovery thirteen times across four threads. The build output does not change mid-run.
    private static readonly Lazy<string[]> Sites = new(
        () => AuditAppendSymbolInventory
            .Discover(Classify)
            .Select(site => $"{site.File}|{site.Symbol}|{site.Kind}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] Discover() => Sites.Value;

    /// <summary>
    /// A construction of an approval fact, by either route the language offers. <c>newobj</c> is the plain
    /// <c>new</c>; <c>&lt;Clone&gt;$</c> is what a <c>with</c> expression emits at the writer, because the
    /// record's copy constructor lives in the record's own compiler-generated clone and not in the caller.
    /// Without the second arm a writer holding a decision fabricates a fact in one token —
    /// <c>decision.SeparationOfDuty with { Outcome = Pass }</c> — and the fence sees nothing.
    /// </summary>
    internal static string? Classify(MethodBase method)
    {
        if (method is not ConstructorInfo && method.Name is not "<Clone>$") return null;
        var declaring = method.DeclaringType?.FullName;
        return declaring is not null && FactTypes.Contains(declaring, StringComparer.Ordinal)
            ? declaring
            : null;
    }

    [Fact(DisplayName = "The fence sees a fact fabricated with a `with` expression, not only with `new`")]
    public void A_with_expression_clone_is_classified_as_a_construction()
    {
        // The reviewer's plant, at the classifier: a `with` on a fact record emits `callvirt <Clone>$`
        // at the writer, and the IL walk already yields call/callvirt. If Classify ignores it, a
        // one-token fabrication is invisible to every assertion above.
        var clone = typeof(SeparationOfDutyFact).GetMethod(
            "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(clone);
        Assert.Equal(
            "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyFact", Classify(clone));
    }

    [Fact]
    public void No_writer_constructs_an_approval_fact_outside_the_engine()
    {
        var offenders = Discover().Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "An approval fact was constructed outside the ADR 0067 clause 2 separation-of-duty engine. "
            + "A writer must carry the engine's decision, not derive a fact from an ambient threshold. "
            + "Offending sites:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_scan_finds_the_engine_itself()
    {
        // Guards the fence against silent vacuity: if the IL walk stopped resolving constructor tokens, or
        // stopped seeing newobj, every assertion above would pass over an empty set.
        var discovered = Discover();

        Assert.Contains(
            discovered,
            site => site.Contains("SeparationOfDutyEngine.Decide", StringComparison.Ordinal)
                && site.EndsWith("SeparationOfDutyFact", StringComparison.Ordinal));
    }

    [Fact]
    public void A_planted_offender_is_rejected()
    {
        // The mutation the fence exists for, run in-test: a writer-shaped construction site that is not on
        // the allow-list must be reported. Planted rather than committed, so the mutation runs every build.
        const string planted =
            "apps/local-node-host/Health/InvoiceRoutes.cs|Harborline.Api.LocalNodeHost.Health.InvoiceRoutes"
            + ".Map(): System.Void|Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalPolicyFact";

        var offenders = Discover().Append(planted).Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.Equal([planted], offenders);
    }
}
