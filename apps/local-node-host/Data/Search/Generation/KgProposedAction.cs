using System.Collections.Generic;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// A STRUCTURED proposed CP action a grounded proposal can carry (ADR 0135 KG-search Slice 2-actions — the
/// human-CP-gate sub-slice). The model can now PROPOSE a guarded change (e.g. "draft this journal entry"); a
/// human gates it. This record is INERT — it is a DESCRIPTION of a proposed action, never an executed one.
/// Nothing in <c>blocks-workflow</c> or this Generation layer acts on it: a proposed action's ONLY exit is the
/// ADR-0135 engine human-CP-park (the <c>GraphRagProposalHandler</c> parks it; only a human approve triggers
/// execution via the EXISTING CP path). See the file-level <see cref="KgGenerationProposal"/> remarks for the
/// G-G4 structural no-autonomous-action property.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a structured kind + a JSON payload, not a free-text instruction.</b> The proposed action is
/// re-presented to a human and (on approve) routed to the EXISTING CP execution path — it must be a
/// machine-checkable, named operation (the engine routes on <see cref="Kind"/>), NOT a natural-language
/// instruction the system "obeys". A model output that could be parsed as an arbitrary command is exactly the
/// injection-amplification this whole slice exists to prevent. The payload is opaque, structured input to a
/// NAMED CP path the host already exposes (e.g. draft-a-JE), never an open-ended "do X" verb.
/// </para>
/// <para>
/// <b>Taint rides WITH the action.</b> A proposed action produced from untrusted grounding is itself untrusted
/// (the grounding could be attacker-influenced — an injected "draft a JE paying attacker"). The action travels
/// inside a taint-labeled <see cref="KgGenerationProposal"/> and is NEVER promoted out of the human-gated path
/// — the human reviews the basis (incl. the taint label + the grounding it cited) BEFORE any execution. The
/// human is the gate against injection-driven actions.
/// </para>
/// </remarks>
/// <param name="Kind">
/// The NAMED CP action kind the host can execute (e.g. <see cref="DraftJournalEntry"/>). The engine routes on
/// this; an unknown kind is refused at the human-CP-park (it has no executor) rather than executed.
/// </param>
/// <param name="Summary">
/// A short human-readable summary of the proposed action (e.g. "Draft JE: Debit AR 1200; Credit Income 4000").
/// Rendered in the park basis BEFORE the confirm control. UNTRUSTED-derived (the model wrote it) — presentation
/// only, never the execution input.
/// </param>
/// <param name="PayloadJson">
/// The structured, machine-checkable input the host CP executor consumes for <see cref="Kind"/> (e.g. the JE
/// lines). Opaque to this layer + the engine; validated by the host executor at approve-time. UNTRUSTED — the
/// host executor re-validates it against the principal's authority before executing (the proposal does NOT
/// carry authority; the approving human's CP path does).
/// </param>
public sealed record KgProposedAction(
    string Kind,
    string Summary,
    string PayloadJson)
{
    /// <summary>The v1 named CP action kind — "draft a journal entry" (a CP financial mutation, human-gated).</summary>
    public const string DraftJournalEntry = "draft-journal-entry";

    /// <summary>The set of NAMED CP action kinds the v1 host can route to an executor. An unknown kind is refused.</summary>
    public static readonly IReadOnlyList<string> KnownKinds = new[] { DraftJournalEntry };

    /// <summary>True iff <see cref="Kind"/> is a known, routable CP action kind (else the park refuses it).</summary>
    public bool IsKnownKind => KnownKindsContains(Kind);

    /// <summary>Case-sensitive membership check against <see cref="KnownKinds"/> (null/blank ⇒ false).</summary>
    public static bool KnownKindsContains(string? kind)
        => !string.IsNullOrWhiteSpace(kind) && KnownKinds.Contains(kind);
}
