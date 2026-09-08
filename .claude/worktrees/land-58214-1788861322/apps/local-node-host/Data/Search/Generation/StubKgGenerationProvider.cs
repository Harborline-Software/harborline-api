using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// The deterministic, self-identifying stub generation provider (ADR 0135 KG-search Slice 2-foundation). Produces
/// a placeholder proposal that cites the grounding record ids (NOT inventing facts, and NEVER echoing the
/// untrusted grounding text) — the SAME envelope shape as a real provider — tagged with the <b>self-identifying
/// sentinel</b> <see cref="KgGenerateFloorGate.StubModelSentinel"/> (<c>stub-qwen2.5</c>), NEVER a registered
/// floor id.
/// </summary>
/// <remarks>
/// <b>The M-G1 no-fake-as-real property lives HERE, at the source.</b> A stub proposal must never be mistaken for
/// a genuine grounded answer, so it self-identifies. A production service rejects a stub-tagged proposal
/// (floor-unavailable); a test exercises the full clip → firewall → proposal chain with it. Even the stub is
/// INERT on an injection — it cites ids, never the untrusted text — so it cannot be a vehicle for surfacing
/// attacker content as if it were an answer.
/// </remarks>
public sealed class StubKgGenerationProvider : IKgGenerationProvider
{
    /// <inheritdoc />
    public string Model => KgGenerateFloorGate.StubModelSentinel;

    /// <inheritdoc />
    public Task<KgGenerationProposal> GenerateAsync(
        string prompt,
        IReadOnlyList<KgGroundingSource> grounding,
        int? maxTokens,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(grounding);

        var ids = grounding.Select(g => g.RecordId).ToArray();
        var cited = ids.Length > 0 ? string.Join(", ", ids) : "no grounding";
        // INERT: cites ids only, never the untrusted grounding text (so the stub can't surface attacker content).
        var text = $"[stub proposal — real generation floor unavailable] re: {prompt} (grounding: {cited})";

        return Task.FromResult(new KgGenerationProposal(
            Text: text,
            // SELF-IDENTIFY — never a registered floor id. The service rejects this as floor-unavailable.
            Model: KgGenerateFloorGate.StubModelSentinel,
            ModelVersion: "stub",
            GroundingRecordIds: ids,
            Taint: KgProposalTaint.UntrustedDerived));
    }
}
