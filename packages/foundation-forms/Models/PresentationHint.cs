namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The presentation payload carried by a <see cref="RuleActionKind.Presentation"/>
/// rule (SPINE-1 Decision DA — an additive ADR-0055 contract extension; a thin
/// Rev-7/8 amendment). Describes a style class / badge / severity to apply when the
/// rule evaluates true. Additive + back-compat: absent on every pre-SPINE-1 rule.
/// </summary>
/// <param name="Severity">One of <c>"info"</c> / <c>"warn"</c> / <c>"error"</c>; null when none.</param>
/// <param name="Badge">An optional localized badge label.</param>
/// <param name="StyleToken">An optional opaque design-token id the renderer maps to a CSS class.</param>
public sealed record PresentationHint(
    string? Severity = null,
    InternationalizedText? Badge = null,
    string? StyleToken = null);
