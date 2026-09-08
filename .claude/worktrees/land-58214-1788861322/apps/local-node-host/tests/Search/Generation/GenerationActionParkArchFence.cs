using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Generation;

/// <summary>
/// <b>GenerationActionParkArchFence</b> — the structural fences for KG-search Slice 2-actions (ADR 0135 — the
/// human-CP-gate for proposed CP actions, G-G4). Proves the load-bearing safety property is STRUCTURAL
/// (un-bypassable), not merely code-reviewed: <b>there is NO path from a generation proposal directly to an
/// action — the ONLY path is via the parked human-task</b> (the model PROPOSES, the human ACTS). The same
/// no-side-door discipline as <see cref="GenerationClipArchFence"/>.
/// <list type="number">
///   <item><b>G-G4</b> — the proposal-producing <see cref="GroundedProposalService"/> has NO autonomous-action
///     verb AND does NOT reference the engine dispatcher / the park cutover (it cannot reach execution);</item>
///   <item><b>G-G4</b> — the ONLY component that consumes a <see cref="KgGenerationProposal"/> to do anything
///     beyond return it is <see cref="NodeKgActionApprovalCutover"/>, whose only proposal verb is
///     <c>ParkForApprovalAsync</c> (it PARKS — there is no <c>Execute</c>/<c>Apply</c>/<c>Send</c>(proposal));</item>
///   <item><b>G-G4</b> — the engine handler's ONLY path to the CP execute effect is the approve human-action
///     (the <c>decide</c> step parks; <c>BuildExecuteEffect</c> is reached only from the approve branch); and</item>
///   <item><b>taint-structural</b> — a generated proposal's only taint is <c>untrusted-derived</c>, and the
///     proposed-action model carries no authority (the approving human's CP path does).</item>
/// </list>
/// </summary>
public sealed class GenerationActionParkArchFence
{
    private static readonly Assembly ProductionAssembly = typeof(GroundedProposalService).Assembly;

    // ── FENCE 1 — the proposal PRODUCER cannot reach execution (no act verb; no dispatcher/cutover ref) ───

    [Fact(DisplayName = "G-G4 fence: GroundedProposalService (the proposal PRODUCER) has NO autonomous-action verb — its only output verb is ProposeAsync → a proposal")]
    public void Proposal_Producer_Has_No_Action_Verb()
    {
        var svcMethods = typeof(GroundedProposalService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        Assert.All(svcMethods, n =>
            Assert.DoesNotMatch(@"(?i)\b(send|apply|act|post|execute|commit|approve|park)\b", n));
        Assert.Contains(nameof(GroundedProposalService.ProposeAsync), svcMethods);
    }

    [Fact(DisplayName = "G-G4 fence: GroundedProposalService does NOT depend on the engine dispatcher or the park cutover — the proposal PRODUCER cannot reach the engine at all")]
    public void Proposal_Producer_Does_Not_Reference_The_Engine()
    {
        // The producer's ctor params + fields must NOT include the dispatcher or the cutover. If the service
        // that PRODUCES proposals could reach the engine/park, a future edit could wire generate→park→execute
        // INSIDE the producer, collapsing the human gate. Keeping the producer engine-free makes the
        // park a SEPARATE, explicit caller step — the structural guarantee that generation never auto-acts.
        var referenced = typeof(GroundedProposalService)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType)
            .Concat(typeof(GroundedProposalService)
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType))
            .ToArray();

        Assert.DoesNotContain(typeof(IWorkflowTriggerDispatcher), referenced);
        Assert.DoesNotContain(typeof(NodeKgActionApprovalCutover), referenced);
        Assert.DoesNotContain(typeof(NodeWorkflowInstantiationService), referenced);
    }

    // ── FENCE 2 — the ONLY proposal consumer is the PARK cutover; its only proposal verb PARKS ────────────

    [Fact(DisplayName = "G-G4 fence: NO method anywhere takes a KgGenerationProposal and EXECUTES/APPLIES/SENDS it; the only PUBLIC proposal-consumer is NodeKgActionApprovalCutover.ParkForApprovalAsync (it PARKS)")]
    public void Only_Proposal_Consumer_Is_The_Park_Cutover()
    {
        // Scan the production assembly for every method (public OR private) that ACCEPTS a KgGenerationProposal
        // as a parameter — excluding the record's own compiler-generated members. The producer RETURNS one (not
        // a param); a consumer ACCEPTS one.
        var consumers = ProductionAssembly
            .GetTypes()
            .Where(t => t != typeof(KgGenerationProposal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(KgGenerationProposal)))
            .Select(m => (Type: m.DeclaringType!.Name, Method: m.Name))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(consumers);

        // (a) NO consumer — public or private — has an autonomous-action verb. There is no method anywhere
        //     that takes a proposal and executes / applies / sends / posts / commits / acts on it.
        Assert.All(consumers, c =>
            Assert.DoesNotMatch(@"(?i)(execute|apply|send|post|commit|act\b|approve)", c.Method));

        // (b) every consumer lives on the park cutover (the single, named gate-entry component) — no OTHER
        //     production type takes a proposal at all (so nothing else can build a generate→act path).
        Assert.All(consumers, c => Assert.Equal(nameof(NodeKgActionApprovalCutover), c.Type));

        // (c) the cutover's only PUBLIC proposal verb is ParkForApprovalAsync (the named gate-entry that PARKS).
        var cutoverPublicProposalVerbs = typeof(NodeKgActionApprovalCutover)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(KgGenerationProposal)))
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal(new[] { nameof(NodeKgActionApprovalCutover.ParkForApprovalAsync) }, cutoverPublicProposalVerbs);
        Assert.Matches(@"(?i)park", NodeKgActionApprovalCutover_ParkVerbName);
    }

    private const string NodeKgActionApprovalCutover_ParkVerbName = nameof(NodeKgActionApprovalCutover.ParkForApprovalAsync);

    [Fact(DisplayName = "G-G4 fence is non-vacuous: the action-verb regex DOES fire on a planted Execute/Apply/Send verb but NOT on Park")]
    public void Action_Verb_Fence_Is_NonVacuous()
    {
        // camelCase has no \b between lower→upper, so the verb regex matches the SUBSTRING (no trailing \b).
        Assert.Matches(@"(?i)(execute|apply|send|post|commit|act\b|approve)", "ExecuteProposalAsync");
        Assert.Matches(@"(?i)(execute|apply|send|post|commit|act\b|approve)", "ApplyProposalAsync");
        Assert.Matches(@"(?i)(execute|apply|send|post|commit|act\b|approve)", "SendProposalAsync");
        Assert.Matches(@"(?i)park", "ParkForApprovalAsync");
        Assert.DoesNotMatch(@"(?i)(execute|apply|send|post|commit|act\b)", "ParkForApprovalAsync");
    }

    // ── FENCE 3 — the engine handler's ONLY path to the execute effect is the approve human-action ────────

    [Fact(DisplayName = "G-G4 fence: the GraphRagProposalHandler decide step ALWAYS parks (no decide→execute path); BuildExecuteEffect is invoked ONLY from the approve human-action branch")]
    public void Handler_Reaches_Execute_Only_Via_Approve()
    {
        // Structural read of the handler source: the only call to the execute-effect builder
        // (IKgActionApprovalContext.BuildExecuteEffect) sits in the approve human-action branch — NOT in the
        // decide step. The decide step parks. We assert on the source so a future edit that moved the effect
        // build into decide (collapsing the gate) trips this fence.
        var src = ReadHandlerSource();

        // The decide entry method (ParkForApproval) must NOT build the execute effect.
        var parkMethod = ExtractMethodBody(src, "ParkForApproval");
        Assert.DoesNotContain("BuildExecuteEffect", parkMethod);

        // The approve resolution (ResolveHumanAction) IS where the execute effect is built — and only on approve.
        var resolveMethod = ExtractMethodBody(src, "ResolveHumanAction");
        Assert.Contains("BuildExecuteEffect", resolveMethod);
        // The approve case precedes the BuildExecuteEffect call (defence: the effect lives under the approve arm).
        var approveIdx = resolveMethod.IndexOf("case \"approve\"", StringComparison.Ordinal);
        var buildIdx = resolveMethod.IndexOf("BuildExecuteEffect", StringComparison.Ordinal);
        Assert.True(approveIdx >= 0 && buildIdx > approveIdx,
            "BuildExecuteEffect must be reached under the approve case (the only path to execute).");
    }

    // ── FENCE 4 — taint is structural: the only taint is untrusted-derived; the action carries no authority ─

    [Fact(DisplayName = "taint fence: a generated proposal's only taint label is UntrustedDerived; the proposed-action model carries no authority field")]
    public void Proposal_Taint_Is_Structural_And_Action_Carries_No_Authority()
    {
        // The taint enum has exactly the untrusted-derived label (no 'trusted' / 'cleared' value a future edit
        // could set to escape the human gate).
        var taintNames = Enum.GetNames(typeof(KgProposalTaint));
        Assert.Equal(new[] { nameof(KgProposalTaint.UntrustedDerived) }, taintNames);

        // The proposed-action model carries NO authority/principal/role/grant — the proposal does not carry
        // authority; the APPROVING human's CP path does. (A model that could carry "I am authorized" would be
        // an injection-amplification surface.)
        var actionProps = typeof(KgProposedAction).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(actionProps, n =>
            System.Text.RegularExpressions.Regex.IsMatch(n, @"(?i)authority|authorized|principal|role|grant|approv"));
    }

    [Fact(DisplayName = "production assembly is Harborline.Api.LocalNodeHost (the fences scan the shipped host)")]
    public void Production_Assembly_Is_The_Host()
    {
        Assert.Equal("Harborline.Api.LocalNodeHost", ProductionAssembly.GetName().Name);
    }

    // ── source-reading helpers (locate the handler source for the decide/approve structural read) ─────────

    private static string ReadHandlerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "packages", "blocks-workflow", "src", "durable", "GraphRagProposalHandler.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate GraphRagProposalHandler.cs from " + AppContext.BaseDirectory);
    }

    private static string ExtractMethodBody(string src, string methodName)
    {
        var sig = "private WorkflowStepOutcome " + methodName + "(";
        var start = src.IndexOf(sig, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method '{methodName}' not found in handler source");

        // Walk braces from the first '{' after the signature to the matching close.
        var open = src.IndexOf('{', start);
        Assert.True(open >= 0);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{')
            {
                depth++;
            }
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return src[open..(i + 1)];
                }
            }
        }
        throw new InvalidOperationException($"unbalanced braces extracting '{methodName}'");
    }
}
