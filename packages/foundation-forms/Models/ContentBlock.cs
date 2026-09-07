namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// One node of a static content block (F-23 — layout breadth). Heading/paragraph-level
/// LOCALIZED PLAIN TEXT only — there is deliberately no html/markup node kind: renderers
/// emit the text as text nodes (fail-closed against markup injection) and
/// <see cref="FormDefinitionValidation"/> rejects any node kind outside
/// <see cref="ContentNodeKinds"/> with a stable code at admission.
/// </summary>
/// <param name="Kind">The node kind — one of <see cref="ContentNodeKinds.Heading"/> /
/// <see cref="ContentNodeKinds.Paragraph"/> (a closed, admission-validated set; carried as
/// the lowercase wire string, mirroring the TS discriminated union).</param>
/// <param name="Text">The localized plain text (never markup).</param>
/// <param name="Level">Heading-level intent (2–6; renderers clamp). Meaningful for a
/// <see cref="ContentNodeKinds.Heading"/> node only; <see langword="null"/> ⇒ the default (3).</param>
public sealed record ContentNode(
    string Kind,
    InternationalizedText Text,
    int? Level = null);

/// <summary>The closed set of <see cref="ContentNode.Kind"/> tokens (F-23). Fail-closed:
/// admission rejects anything else (<c>form.blocks.content_unknown_node_kind</c>).</summary>
public static class ContentNodeKinds
{
    public const string Heading = "heading";
    public const string Paragraph = "paragraph";

    /// <summary>Is <paramref name="kind"/> a declared content-node kind?</summary>
    public static bool IsKnown(string? kind) => kind is Heading or Paragraph;
}

/// <summary>
/// The declarative config of an action block (F-23) — a localized label + a BOUNDED
/// action kind + its single target. A definition carries CONFIG ONLY, never imperative
/// code (ratified Decision 1; mirrors the <c>OnSuccessConfig</c> precedent):
/// <list type="bullet">
///   <item><see cref="FormActionKinds.OpenUrl"/> — consumed by the HOST (a renderer never
///     navigates itself); admission requires an absolute http(s) <paramref name="Url"/>,
///     fail-closed against <c>javascript:</c>/<c>data:</c>/relative payloads.</item>
///   <item><see cref="FormActionKinds.ScrollToSection"/> — the renderer scrolls to + focuses
///     the target section; admission requires a declared <paramref name="SectionId"/>.</item>
/// </list>
/// </summary>
/// <param name="Kind">One of <see cref="FormActionKinds"/> (closed, admission-validated;
/// carried as the lowercase wire string).</param>
/// <param name="Label">Localized button label.</param>
/// <param name="Url"><c>open-url</c> only — an absolute http(s) URL.</param>
/// <param name="SectionId"><c>scroll-to-section</c> only — a declared section id.</param>
public sealed record FormActionConfig(
    string Kind,
    InternationalizedText Label,
    string? Url = null,
    string? SectionId = null);

/// <summary>The closed set of <see cref="FormActionConfig.Kind"/> tokens (F-23). Fail-closed:
/// admission rejects anything else (<c>form.blocks.action_unknown_kind</c>) — the set stays
/// small and analyzable by design.</summary>
public static class FormActionKinds
{
    public const string OpenUrl = "open-url";
    public const string ScrollToSection = "scroll-to-section";

    /// <summary>Is <paramref name="kind"/> a declared action kind?</summary>
    public static bool IsKnown(string? kind) => kind is OpenUrl or ScrollToSection;
}
