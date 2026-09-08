using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// A registered REAL embedding floor (ADR 0135 KG-search F3-lift amendment, Slice 1b; ADR 0123 <c>embeddings</c>
/// capability kind). The set of <see cref="KgModelFloor"/>s is the allow-list the indexer's <b>M1 no-fake-as-real
/// gate</b> checks an artifact's <c>model</c> against: only an artifact whose <c>model</c> equals a registered
/// floor id (and whose vector width matches that floor's <see cref="Dimension"/>) is allowed into the durable
/// index. Anything else — a stub sentinel, an unknown id, a wrong-width vector — is rejected.
/// </summary>
/// <remarks>
/// Mirrors the capability <c>KgModelFloor</c> (apps/capability-host/src/runtime/kg-embed-model.ts): same id (<c>bge-m3</c>),
/// same dimension (1024). The two definitions are kept byte-aligned so the .NET index pins the SAME model
/// provenance the TS runtime stamps on its artifact (G-5).
/// </remarks>
/// <param name="Id">The floor id — the value an artifact's <c>model</c> must equal to be indexable (e.g. <c>bge-m3</c>).</param>
/// <param name="Dimension">The declared embedding width — an artifact whose vector width differs is a hard fault (G-5).</param>
/// <param name="DefaultModelVersion">The model revision the floor produces (G-5).</param>
public sealed record KgModelFloor(string Id, int Dimension, string DefaultModelVersion)
{
    /// <summary>
    /// The <c>embeddings</c> floor — BGE-M3 (BAAI/bge-m3), MIT, 1024-dim. The single registered real floor in
    /// Slice 1b; the ONLY <c>model</c> id an artifact may carry to enter the index.
    /// </summary>
    public static KgModelFloor BgeM3 { get; } = new("bge-m3", Dimension: 1024, DefaultModelVersion: "1.0");

    /// <summary>The registered real floors (the M1 allow-list). Slice 1b registers only <see cref="BgeM3"/>.</summary>
    public static IReadOnlyList<KgModelFloor> Registered { get; } = new[] { BgeM3 };
}

/// <summary>
/// The M1 no-fake-as-real gate (ADR 0135 KG-search F3-lift amendment, Slice 1b) — the SOLE arbiter of whether
/// an embedding artifact's claimed <c>model</c> is a genuine registered floor. Static + pure so it is trivially
/// arch-testable and cannot be bypassed by a mis-wired DI graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the model id is fenced (the no-fake-as-real property).</b> The capability Slice-1a runtime stamps
/// <c>model: 'bge-m3'</c> on BOTH its real worker output AND its deterministic CI stub — so a stub vector is
/// indistinguishable from a genuine BGE-M3 vector by its label alone (a latent gap tracked for Slice 1d to fix
/// at source). Embeddings are partially invertible, so a fake vector that pins as <c>bge-m3</c> in the durable
/// index is a trust leak (the no-mock-crypto family, bug-1312). This gate closes it at the durable boundary:
/// the indexer admits a row ONLY if <see cref="IsRegisteredFloor"/> AND the vector width matches the floor's
/// dimension. The .NET deterministic provider self-identifies as <c>stub-bge-m3</c> (NOT a registered floor),
/// so it is rejected by a production indexer; a test indexer must explicitly opt into stub-indexing to admit
/// it. A non-opted-in host with no real provider fails closed (<c>provider.kg_floor_unavailable</c>) rather
/// than silently indexing fakes.
/// </para>
/// </remarks>
public static class KgModelFloorGate
{
    /// <summary>The self-identifying sentinel a deterministic stub provider stamps — NEVER a registered floor.</summary>
    public const string StubModelSentinel = "stub-bge-m3";

    /// <summary>True when <paramref name="model"/> is a registered REAL floor id (the M1 allow-list).</summary>
    public static bool IsRegisteredFloor(string? model) =>
        model is not null
        && KgModelFloor.Registered.Any(f => string.Equals(f.Id, model, StringComparison.Ordinal));

    /// <summary>Resolves the registered floor for <paramref name="model"/>, or null when it is not a real floor.</summary>
    public static KgModelFloor? ResolveFloor(string? model) =>
        model is null
            ? null
            : KgModelFloor.Registered.FirstOrDefault(f => string.Equals(f.Id, model, StringComparison.Ordinal));
}

/// <summary>
/// Thrown by the vector indexer's M1 gate when an embedding artifact is REFUSED — its <c>model</c> is not a
/// registered real floor, or its vector width does not match the floor's declared dimension (G-5). A
/// production host that has no real provider also surfaces this via the <c>provider.kg_floor_unavailable</c>
/// reason rather than indexing an unverifiable artifact.
/// </summary>
public sealed class KgFloorUnavailableException : Exception
{
    /// <summary>The stable reason code (mirrors the capability failure-envelope code <c>provider.kg_floor_unavailable</c>).</summary>
    public const string ReasonCode = "provider.kg_floor_unavailable";

    /// <summary>The model id that was refused (the artifact's claimed <c>model</c>).</summary>
    public string RefusedModel { get; }

    /// <summary>Construct with the refused model + a human-readable reason.</summary>
    public KgFloorUnavailableException(string refusedModel, string reason)
        : base($"{ReasonCode}: {reason} (model='{refusedModel}')")
    {
        RefusedModel = refusedModel;
    }
}
