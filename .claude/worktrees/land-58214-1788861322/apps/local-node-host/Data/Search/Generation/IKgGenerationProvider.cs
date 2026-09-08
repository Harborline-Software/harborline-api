using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// The .NET-side GENERATION-provider seam (ADR 0135 KG-search Slice 2-foundation — the safe interim generative
/// GraphRAG: proposal-only / human-CP-gated / §2.8.4-firewall-bound) — produces a grounded TEXT PROPOSAL + its
/// model provenance from a permission-clipped grounding subgraph. This is the boundary the
/// <see cref="GroundedProposalService"/> drives the firewall-bound LLM through; it deliberately abstracts over
/// WHERE the generation comes from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The novel risk this seam bounds — the firewall, extended to RETRIEVED text.</b> The grounding fed to
/// <see cref="GenerateAsync"/> is UNTRUSTED: an indexed email/attachment/transcript may carry a STORED
/// prompt-injection that detonates at generation. The real provider runs the model in the G-4 OS-native sandbox
/// (no tools, no egress, no DEK reach) so the model reading attacker text <b>has no hands</b> — a successful
/// injection yields a <i>suggestion</i> (the returned proposal text), never an action or an exfiltration. The
/// output is taint-propagating: it re-enters the human-gated path and is NEVER auto-applied or auto-sent
/// (proposal-only — the autonomous form stays broker-PEP-gated by ratified design, NOT built here).
/// </para>
/// <para>
/// <b>Two implementations, one fail-closed contract.</b>
/// <list type="bullet">
///   <item>The REAL provider (<see cref="Cli.KgCliGenerationProvider"/>) drives the capability <c>generate</c>
///     runtime (Qwen2.5-7B-Instruct, Apache-2.0, on the G-4 sandboxed CPU floor) over the
///     <c>kg-generate</c> CLI (CLI(<c>--json</c>) + SDK, NOT MCP) and returns a proposal tagged with the
///     real floor id.</item>
///   <item>The deterministic <see cref="StubKgGenerationProvider"/> returns a placeholder proposal tagged with
///     the SELF-IDENTIFYING sentinel (<c>stub-qwen2.5</c>), so a stub can NEVER pass as a genuine grounded
///     answer; the service treats a stub-tagged proposal as floor-unavailable.</item>
/// </list>
/// </para>
/// <para>
/// <b>Fail-closed when no provider.</b> A production host with no real provider registered does NOT silently
/// fall back to the stub for a real ask; the composition leaves the service with no provider and the
/// grounded-proposal path surfaces <see cref="KgGenerateFloorUnavailableException"/>
/// (<c>provider.kg_generate_floor_unavailable</c>) rather than emitting an unverifiable proposal as genuine.
/// </para>
/// </remarks>
public interface IKgGenerationProvider
{
    /// <summary>The model id this provider tags its proposals with — a registered floor id (real) or the stub sentinel.</summary>
    string Model { get; }

    /// <summary>
    /// Produce a grounded TEXT proposal answering <paramref name="prompt"/> FROM <paramref name="grounding"/>.
    /// The grounding is UNTRUSTED, permission-clipped retrieved content (the firewall + sandbox bound what the
    /// model can do with it). The result is a PROPOSAL the caller reviews — NEVER acted on.
    /// </summary>
    /// <param name="prompt">The user's question (the trusted task).</param>
    /// <param name="grounding">The permission-clipped, untrusted grounding sources (always pre-authorized).</param>
    /// <param name="maxTokens">Soft cap on the proposal length (null ⇒ provider default).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<KgGenerationProposal> GenerateAsync(
        string prompt,
        IReadOnlyList<KgGroundingSource> grounding,
        int? maxTokens,
        CancellationToken ct = default);
}

/// <summary>
/// One permission-clipped grounding source fed to the generation provider — an AUTHORIZED record's text + the
/// edge provenance (ADR 0135 KG-search Slice 2-foundation, G-G2). Always assembled from an
/// <see cref="AuthorizedRecordScope"/> by the <see cref="GroundingAssembler"/>, so <see cref="RecordId"/> is
/// ALWAYS authorized; the <see cref="Text"/> is UNTRUSTED (may carry a stored injection — taint-labeled).
/// </summary>
/// <param name="RecordId">The authorized record this grounding came from (the clipped set's narrowing key; rides the basis).</param>
/// <param name="Text">The record's text the model grounds on. UNTRUSTED — may carry a stored prompt-injection.</param>
/// <param name="Asserted">
/// True for an ASSERTED edge (a fact, from the records); false for an INFERRED edge (an AI hint — §2.9 det/ai
/// split, never authoritative for a guarded action). The proposal carries this in its basis.
/// </param>
public readonly record struct KgGroundingSource(string RecordId, string Text, bool Asserted);

/// <summary>
/// A grounded proposal (ADR 0135 KG-search Slice 2-foundation + Slice 2-actions). The model's answer + its
/// provenance + the grounding-path basis + the TAINT label, and — for Slice 2-actions — an OPTIONAL structured
/// proposed CP <see cref="Action"/>. It is a PROPOSAL the caller reviews — NEVER an autonomous action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two output classes (the safe interim, survey PART 2).</b> A proposal whose <see cref="Action"/> is
/// <see langword="null"/> is an INERT-TEXT Q&amp;A proposal — it renders directly to the requesting user
/// (firewall-bound + taint-labeled, no autonomous effect; the Slice 2-foundation path, unchanged). A proposal
/// carrying a non-null <see cref="Action"/> is a PROPOSED CP ACTION — it MUST park to the ADR-0135 engine
/// human-CP-gate (the <c>GraphRagProposalHandler</c>); the model PROPOSES, the human ACTS.
/// </para>
/// <para>
/// <b>G-G4 — structural no-autonomous-action.</b> Carrying an <see cref="Action"/> does NOT make this type
/// actionable: there is NO execute/apply/send verb on this record (it is an inert <c>record</c>) and no method
/// anywhere takes a <see cref="KgGenerationProposal"/> and performs its action. The ONLY path from a proposed
/// action to execution is the parked human-task — an approve human-action resumes the engine, which routes the
/// action to the EXISTING CP executor "as the human". A reject does nothing. This is arch-tested
/// (no direct proposal→action path).
/// </para>
/// </remarks>
/// <param name="Text">The grounded proposal text (a suggestion, never an instruction the system obeys).</param>
/// <param name="Model">The model that produced it (a registered floor id, or the stub sentinel).</param>
/// <param name="ModelVersion">The model version/revision.</param>
/// <param name="GroundingRecordIds">The authorized record ids the proposal was grounded on (the FE-1 / §2.9 basis).</param>
/// <param name="Taint">
/// The taint label — always <see cref="KgProposalTaint.UntrustedDerived"/> for a generated proposal (produced
/// from untrusted retrieved grounding; re-enters the human path, never auto-applied/auto-sent). The taint rides
/// the <see cref="Action"/> (if any) through the human-CP-park and into the durable-layer audit on approve.
/// </param>
/// <param name="Action">
/// The OPTIONAL structured proposed CP action (Slice 2-actions). <see langword="null"/> ⇒ an inert-text Q&amp;A
/// proposal (the Slice 2-foundation path). Non-null ⇒ a proposed CP action that MUST park to the human-CP-gate
/// — it is NEVER executed without a human approve (G-G4). Inert by construction: a description of an action,
/// not an executed one.
/// </param>
public sealed record KgGenerationProposal(
    string Text,
    string Model,
    string ModelVersion,
    IReadOnlyList<string> GroundingRecordIds,
    KgProposalTaint Taint,
    KgProposedAction? Action = null)
{
    /// <summary>True iff this proposal carries a proposed CP action (⇒ it MUST park to the human-CP-gate).</summary>
    public bool IsProposedAction => Action is not null;
}

/// <summary>The taint label a generated proposal carries (the §2.8.4 firewall obligation).</summary>
public enum KgProposalTaint
{
    /// <summary>
    /// Produced from UNTRUSTED retrieved grounding — a PROPOSAL re-entering the human-gated path, NEVER an
    /// autonomous action and NEVER auto-sent outbound (the firewall's "reviewed before it leaves").
    /// </summary>
    UntrustedDerived = 0,
}
