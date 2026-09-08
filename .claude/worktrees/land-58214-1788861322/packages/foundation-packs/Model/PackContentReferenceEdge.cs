using Harborline.Api.Foundation.Packs.Graph;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// One declared, signed, content-grain cross-app reference an app makes into another app's contributable
/// (app-layer design note §6.3, slice G2 — the one manifest schema change). Today <c>Dependencies[]</c> is
/// <b>pack-level</b> ("Fleet Ops composes-over Core Records"); this edge is the <b>content grain</b> — "Fleet
/// Ops' <c>Vehicle</c> type builds on Core Records' <c>Property</c> type" — so install preview can say
/// "requires Core Records' Property type" and FAIL CLOSED if it is absent, WITHOUT the opaque install engine
/// parsing bodies. The Composer emits these at compose time from the parsed content
/// (<see cref="Harborline.Api.Foundation.Packs.Export.PackContentReferenceDeriver"/>); the install engine validates
/// them fail-closed; <c>InstalledPack</c> persists them so the graph's class-3 edge survives WITHOUT a body
/// re-parse.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-app only.</b> Only references whose <see cref="ToPackKey"/> differs from the pack's own key are
/// emitted — an intra-pack reference (a form binding this pack's own type) is internal wiring, not a
/// cross-app edge, and is dropped by the deriver (§6.3).
/// </para>
/// <para>
/// <b>Signed.</b> The edge lives INSIDE the signed <see cref="PackManifest"/> (via
/// <c>PackSignatureSubject</c>), so a pack's declared cross-app dependencies are signer-controlled and
/// unforgeable — an attacker cannot strip or forge an edge without invalidating the signature. The field is
/// OPTIONAL (<c>null</c> ⇒ a pre-G2 / dependency-free pack) and, being additive to the signed manifest, a
/// pack that carries no edges serializes byte-identically to a pre-G2 manifest (the <c>WhenWritingNull</c>
/// omission on <see cref="PackManifest.ContentReferences"/>), so existing signatures still verify.
/// </para>
/// <para>
/// <b>Relation vocabulary.</b> Reuses <see cref="PackFeatureEdgeRelation"/> — the G1 read-model's edge
/// relation enum, which its author positioned as "the G2 vocab" so the derived-today edge and the
/// declared-in-G2 edge speak ONE vocabulary (no A4 two-vocab drift). Only the content-reference subset
/// (<see cref="PackFeatureEdgeRelation.BindsTo"/> / <see cref="PackFeatureEdgeRelation.ParentOf"/> /
/// <see cref="PackFeatureEdgeRelation.Anchors"/> / <see cref="PackFeatureEdgeRelation.References"/>) is valid
/// here; the pack-level <see cref="PackFeatureEdgeRelation.ComposesOver"/> is a <c>Dependencies[]</c> edge,
/// never a content reference (the composer never emits it).
/// </para>
/// </remarks>
/// <param name="FromContentKey">The referencing contributable's key — a leaf in THIS pack.</param>
/// <param name="FromKind">The referencing contributable's kind.</param>
/// <param name="ToPackKey">The app the reference binds INTO (a DIFFERENT pack key).</param>
/// <param name="ToContentKey">The specific contributable in <see cref="ToPackKey"/> that is referenced.</param>
/// <param name="ToKind">The referenced contributable's kind when the composer can determine it from the
/// relation (e.g. a <see cref="PackFeatureEdgeRelation.ParentOf"/> always targets a type), else <c>null</c>
/// — the graph resolves the concrete kind from install state at read time.</param>
/// <param name="Relation">The reference relation (the content-reference subset of the shared vocab).</param>
public sealed record PackContentReferenceEdge(
    string FromContentKey,
    PackContentKind FromKind,
    string ToPackKey,
    string ToContentKey,
    PackContentKind? ToKind,
    PackFeatureEdgeRelation Relation);
