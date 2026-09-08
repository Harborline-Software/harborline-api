using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// The firewall-bound, proposal-only grounded-generation service (ADR 0135 KG-search Slice 2-foundation — the
/// safe interim generative GraphRAG: proposal-only / human-CP-gated / §2.8.4-firewall-bound). It wires the full
/// SAFE chain end to end:
/// <code>
///   retrieve permission-clipped grounding (G-G2, the Slice-1 clip holds through grounding)
///     → assemble the grounding from the AUTHORIZED set only (GroundingAssembler)
///     → the §2.8.4 firewall bounds the generation: the sandboxed LLM reads UNTRUSTED grounding but has no hands
///     → return a TEXT PROPOSAL (taint-labeled) to the caller — the caller does NOT act on it
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>PROPOSAL-ONLY (the safe interim). The output is a PROPOSAL, never an action.</b> This service returns a
/// <see cref="KgGenerationProposal"/> to its caller; it does NOT send, post, apply, or act on it. The autonomous
/// form (the model acting on its own proposal) requires the broker-PEP + the agentic guard, which do not exist as
/// runtime (ADR 0128 classification-only, ADR 0130 shadow-only) — so it is NOT built here, by ratified design.
/// The CP-park (parking a proposed CP mutation to a human-task) is the NEXT sub-slice (2-actions); this
/// foundation slice stops at the text proposal returned to the caller.
/// </para>
/// <para>
/// <b>The §2.8.4 firewall, extended to RETRIEVED text (G-G3, the novel risk).</b> The grounding is UNTRUSTED
/// inbound content surfaced later — a stored injection in an indexed email/PDF/transcript detonates at
/// generation. The firewall bounds it: the generation worker runs in the G-4 sandbox (no tools, no egress, no DEK
/// reach — enforced at the OS level by the capability runtime), so the model reading attacker text HAS NO HANDS. A
/// successful injection yields a <i>suggestion</i> (the proposal text), never an action or an exfiltration. The
/// returned proposal is taint-<see cref="KgProposalTaint.UntrustedDerived"/> — it re-enters the human path and is
/// never auto-sent.
/// </para>
/// <para>
/// <b>M-G1 fail-closed (no-fake-as-real).</b> The service treats a proposal as a genuine grounded answer ONLY if
/// its model is a registered floor (<see cref="KgGenerateFloorGate.IsRegisteredFloor"/>). A non-armed host's
/// provider returns a self-identifying stub (<c>stub-qwen2.5</c>); the service REJECTS it with
/// <see cref="KgGenerateFloorUnavailableException"/> rather than surfacing a fabricated answer as genuine. A host
/// with NO provider also fails closed (the provider is null).
/// </para>
/// </remarks>
public sealed class GroundedProposalService
{
    private readonly NodeVecSearchReadService _readService;
    private readonly GroundingAssembler _assembler;
    private readonly IKgGenerationProvider? _provider;

    /// <summary>Default number of grounding rows retrieved for a proposal.</summary>
    public const int DefaultGroundingLimit = 8;

    /// <summary>
    /// Construct bound to the clipped read service (grounding retrieval), the grounding assembler (G-G2), and the
    /// generation provider. <paramref name="provider"/> may be null ⇒ the service fails closed
    /// (<see cref="KgGenerateFloorUnavailableException"/>) on every ask — a production host with no real floor
    /// does NOT silently emit fabricated proposals.
    /// </summary>
    public GroundedProposalService(
        NodeVecSearchReadService readService,
        GroundingAssembler assembler,
        IKgGenerationProvider? provider = null)
    {
        _readService = readService ?? throw new ArgumentNullException(nameof(readService));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _provider = provider;
    }

    /// <summary>
    /// Produce a grounded TEXT PROPOSAL answering <paramref name="prompt"/> for <paramref name="principalId"/> in
    /// <paramref name="tenantId"/> — the full firewall-bound, proposal-only chain. The returned proposal is
    /// taint-labeled and is NEVER acted on by this service; the caller receives it for human review.
    /// </summary>
    /// <param name="tenantId">The tenant the ask is scoped to.</param>
    /// <param name="principalId">The asking principal (their authorization clips the grounding — G-G2).</param>
    /// <param name="prompt">The user's question (the trusted task).</param>
    /// <param name="at">The snapshot instant the clip resolves grants at (G-2).</param>
    /// <param name="maxTokens">Soft cap on the proposal length (null ⇒ provider default).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KgGenerateFloorUnavailableException">
    /// No real generation floor (no provider, or a stub/unverifiable proposal) — fail closed (M-G1).
    /// </exception>
    public async Task<KgGenerationProposal> ProposeAsync(
        TenantId tenantId,
        ActorId principalId,
        string prompt,
        DateTimeOffset at,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);

        // M-G1 fail-closed: no provider ⇒ no genuine answer is possible. Fail closed rather than fabricate.
        if (_provider is null)
        {
            throw new KgGenerateFloorUnavailableException(
                "(none)", "no generation provider registered on this host");
        }

        // (1) + (2) — retrieve the permission-clipped grounding (G-G2: the Slice-1 clip holds through grounding)
        //             and assemble it from the AUTHORIZED set only. A forbidden record never reaches the model.
        var clipped = await _readService
            .RetrieveClippedGroundingAsync(tenantId, principalId, prompt, at, DefaultGroundingLimit, ct)
            .ConfigureAwait(false);
        IReadOnlyList<KgGroundingSource> grounding = _assembler.Assemble(clipped);

        // (3) — the firewall bounds the generation: the sandboxed LLM reads the UNTRUSTED grounding but has no
        //       hands (no tools / no egress / no DEK reach — enforced by the capability G-4 sandbox). An injected
        //       instruction in the grounding can only influence the returned TEXT, never cause an action.
        var proposal = await _provider
            .GenerateAsync(prompt, grounding, maxTokens, ct)
            .ConfigureAwait(false);

        // M-G1: reject a stub / unverifiable proposal — never surface a fabricated answer as genuine.
        if (!KgGenerateFloorGate.IsRegisteredFloor(proposal.Model))
        {
            throw new KgGenerateFloorUnavailableException(
                proposal.Model,
                "the generation provider returned a non-floor (stub/unverifiable) proposal — refusing to surface "
                + "it as a genuine grounded answer");
        }

        // (4) — return the taint-labeled PROPOSAL to the caller. PROPOSAL-ONLY: this service does NOT act on it,
        //       send it, or apply it. The taint guarantees the caller treats it as a suggestion re-entering the
        //       human path, never an autonomous action (the autonomous + CP-park forms are later, gated slices).
        return proposal;
    }
}
