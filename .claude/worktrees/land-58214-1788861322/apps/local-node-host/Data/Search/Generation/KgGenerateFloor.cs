using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// A registered REAL generation floor (ADR 0135 KG-search Slice 2-foundation; the ADR 0123 <c>llm</c> capability
/// slot — NO new license decision: Qwen2.5 / Mistral are Apache-2.0, Phi-3 is MIT, per the 2026-06-20 weight
/// sweep + ADR 0132; Llama-community is swap-only, NEVER the floor). The set of <see cref="KgGenerateFloor"/>s is
/// the allow-list the <b>M-G1 no-fake-as-real gate</b> checks a proposal's <c>model</c> against: only a proposal
/// whose <c>model</c> equals a registered floor id is treated as a genuine grounded answer. A stub sentinel /
/// unknown id is rejected (treated as floor-unavailable), never surfaced as a real answer.
/// </summary>
/// <remarks>
/// Mirrors the capability generation floor (apps/capability-host/src/runtime/kg-generate-model.ts): same id
/// (<c>qwen2.5-7b-instruct</c>). The two definitions are kept aligned so the .NET service pins the SAME model
/// provenance the TS runtime stamps on its artifact.
/// </remarks>
/// <param name="Id">The floor id — the value a proposal's <c>model</c> must equal to be treated as genuine.</param>
/// <param name="DefaultModelVersion">The model revision the floor produces.</param>
public sealed record KgGenerateFloor(string Id, string DefaultModelVersion)
{
    /// <summary>
    /// The <c>generate</c> floor — Qwen2.5-7B-Instruct, Apache-2.0. The single registered real generation floor
    /// in Slice 2-foundation; the ONLY <c>model</c> id a proposal may carry to be treated as a real answer.
    /// (Mistral-7B Apache-2.0 / Phi-3 MIT are clean alt floors swappable behind the manifest without a license
    /// decision; Llama-community is swap-only — NEVER added here as a floor.)
    /// </summary>
    public static KgGenerateFloor Qwen25 { get; } = new("qwen2.5-7b-instruct", DefaultModelVersion: "1.0");

    /// <summary>The registered real floors (the M-G1 allow-list). Slice 2-foundation registers only <see cref="Qwen25"/>.</summary>
    public static IReadOnlyList<KgGenerateFloor> Registered { get; } = new[] { Qwen25 };
}

/// <summary>
/// The M-G1 no-fake-as-real gate for GENERATION (ADR 0135 KG-search Slice 2-foundation) — the SOLE arbiter of
/// whether a proposal's claimed <c>model</c> is a genuine registered floor. Static + pure so it is trivially
/// arch-testable and cannot be bypassed by a mis-wired DI graph (the same discipline as the embedding
/// <c>KgModelFloorGate</c>).
/// </summary>
/// <remarks>
/// <b>Why the model id is fenced (the no-fake-as-real property).</b> A non-armed host's runtime emits a
/// deterministic STUB proposal tagged with the self-identifying sentinel (<c>stub-qwen2.5</c>). If a stub
/// proposal were surfaced to a user as a genuine grounded answer, the user would trust a fabricated answer that
/// no model produced (the no-mock-crypto / no-fake-as-real family, bug-1312). This gate closes it: the service
/// treats a proposal as a real answer ONLY if <see cref="IsRegisteredFloor"/>; a stub-tagged / unknown proposal
/// is rejected (floor-unavailable). A non-opted-in host with no real provider fails closed
/// (<c>provider.kg_generate_floor_unavailable</c>) rather than fabricating an answer.
/// </remarks>
public static class KgGenerateFloorGate
{
    /// <summary>The self-identifying sentinel a deterministic stub provider stamps — NEVER a registered floor.</summary>
    public const string StubModelSentinel = "stub-qwen2.5";

    /// <summary>True when <paramref name="model"/> is a registered REAL floor id (the M-G1 allow-list).</summary>
    public static bool IsRegisteredFloor(string? model) =>
        model is not null
        && KgGenerateFloor.Registered.Any(f => string.Equals(f.Id, model, StringComparison.Ordinal));
}

/// <summary>
/// Thrown when no real generation floor is available (ADR 0135 KG-search Slice 2-foundation) — a production host
/// with no provider, or a provider that returned a stub/unverifiable proposal. The grounded-proposal path fails
/// closed with this rather than surfacing a fabricated answer as genuine (M-G1; the no-fake-as-real family).
/// </summary>
public sealed class KgGenerateFloorUnavailableException : Exception
{
    /// <summary>The model id the unavailable/unverifiable floor reported (the stub sentinel or a missing provider).</summary>
    public string Model { get; }

    /// <summary>Construct with the offending model id + a reason.</summary>
    public KgGenerateFloorUnavailableException(string model, string reason)
        : base($"provider.kg_generate_floor_unavailable ({model}): {reason}")
    {
        Model = model;
    }
}
