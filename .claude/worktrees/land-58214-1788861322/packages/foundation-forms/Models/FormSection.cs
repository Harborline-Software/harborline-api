using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// A grouping of fields within a <see cref="FormDefinition"/> (ADR 0055
/// §"Section-based permissions"; composes the dynamic-forms-authorization
/// UPF Approach F).
/// </summary>
/// <remarks>
/// <para>
/// Sections are the unit at which authorization is enforced. The form engine
/// intersects the section list with the actor's macaroon scope (ADR 0032);
/// a vendor or sub-tenant collaborator sees only the sections their token
/// authorizes, with the rest collapsed or omitted entirely depending on
/// the rendering surface. Field-level annotations remain available as an
/// escape hatch (declared in <see cref="FieldOverlay"/>) but section-level
/// is the canonical posture.
/// </para>
/// </remarks>
/// <param name="Id">Stable section identifier, unique within the owning
/// <see cref="FormDefinition"/>. Recommended format <c>{purpose}</c>
/// (<c>tenancy</c>, <c>financial</c>, <c>maintenance</c>). MUST be non-empty.</param>
/// <param name="Title">Localized section title.</param>
/// <param name="Fields">Ordered list of field names that belong to this
/// section. Field names MUST be valid JSON property names within the
/// schema's <c>JsonSchema</c> document.</param>
/// <param name="Access">Read / write authorization for this section.</param>
/// <param name="Layout">Optional 2D layout for this section (ADR 0055 Rev 6 —
/// input-group flex/grid layout). <see langword="null"/> ⇒ stack (one field per
/// row, the legacy behaviour). Presentation-only; does NOT affect
/// <paramref name="Access"/> or the validation / rule surface.</param>
/// <param name="FieldPlacement">Optional per-field placement (grid colSpan /
/// flex grow) keyed by field name, honoured only for a flex/grid
/// <paramref name="Layout"/>.</param>
/// <param name="Aspects">Optional SPINE-2 (ADR 0140 D2) section-grain aspect overlay.
/// Additive — absent ⇒ byte-identical pre-SPINE-2 behaviour. The SPINE-2 resolver
/// treats it as a coarser grain than a field overlay and finer than the form.</param>
/// <param name="Items">Optional recursive form-item tree for this section (ADR 0055
/// Rev 7 — nested sub-form items). When present, the section's structure is the tree
/// (nested <see cref="FormItemKind.Group"/> / <see cref="FormItemKind.Collection"/>
/// containers to arbitrary depth); <see cref="Fields"/> is then the flat
/// authorization/order fallback. Absent (⇒ <see langword="null"/>) is a depth-1 flat
/// section — byte-identical to a pre-Rev-7 definition. The tree is fail-closed bounded
/// at authoring (<see cref="FormTreeLimits"/>, INV-S2 extension). <c>Field</c> items
/// reference the SAME <see cref="HarborlineOverlay.Fields"/> registry as the flat list.</param>
public sealed record FormSection(
    string Id,
    InternationalizedText Title,
    IReadOnlyList<string> Fields,
    SectionAccess Access,
    SectionLayout? Layout = null,
    IReadOnlyDictionary<string, FieldPlacement>? FieldPlacement = null,
    AspectOverlay? Aspects = null,
    IReadOnlyList<FormItem>? Items = null);

/// <summary>
/// Kind of 2D arrangement for a <see cref="FormSection"/>'s fields
/// (ADR 0055 Rev 6 — input-group flex/grid layout). Absent ⇒ <see cref="Stack"/>.
/// </summary>
public enum SectionLayoutKind
{
    /// <summary>Vertical column, one field per row (the legacy / default behaviour).</summary>
    Stack = 0,

    /// <summary>CSS flexbox — <c>direction</c> / <c>wrap</c> / <c>gap</c>.</summary>
    Flex = 1,

    /// <summary>CSS grid — equal-width column tracks + <c>gap</c>; fields may span tracks.</summary>
    Grid = 2,
}

/// <summary>Flexbox main-axis direction (logical, not physical — the renderer
/// maps it to a logical-property utility so it is RTL-correct).</summary>
public enum FlexDirection
{
    Row = 0,
    Column = 1,
}

/// <summary>Flex wrapping mode.</summary>
public enum FlexWrap
{
    Wrap = 0,
    NoWrap = 1,
}

/// <summary>
/// 2D layout configuration for a <see cref="FormSection"/> (ADR 0055 Rev 6).
/// Presentation-only — it does NOT touch <see cref="SectionAccess"/> (the
/// security-load-bearing half) or the validation / rule surface. Projected onto
/// the rendered <c>FormViewSection.Layout</c> and honoured by the React
/// <c>SchemaForm</c> renderer (flex/grid via logical-property utilities, RTL-safe).
/// </summary>
/// <param name="Kind">The arrangement kind. Absent ⇒ <see cref="SectionLayoutKind.Stack"/>.</param>
/// <param name="Direction">Flex-only main-axis direction (default <see cref="FlexDirection.Row"/>).</param>
/// <param name="Wrap">Flex-only wrap behaviour (default <see cref="FlexWrap.Wrap"/>).</param>
/// <param name="Columns">Grid-only equal-width track count, 1–4 (default 2).</param>
/// <param name="Gap">Spacing token step between items: 0,1,2,3,4,5,6,8 (default 4).</param>
/// <param name="CollapseBelow">F-23 responsive intent: collapse a flex/grid arrangement to a
/// single-column stack when the FORM is narrower than this container-relative breakpoint
/// token — one of <see cref="LayoutIntents.Breakpoints"/> (<c>sm</c>/<c>md</c>/<c>lg</c>;
/// closed set, admission-validated; NEVER a pixel value). Null ⇒ no collapse (prior behaviour,
/// byte-identical on the wire).</param>
/// <param name="Density">F-23 vertical-rhythm density intent — one of
/// <see cref="LayoutIntents.Densities"/> (<c>comfortable</c>/<c>compact</c>). Null ⇒ comfortable.</param>
/// <param name="Align">F-23 cross-axis alignment intent for the laid-out items — one of
/// <see cref="LayoutIntents.Aligns"/> (<c>start</c>/<c>center</c>/<c>end</c>/<c>stretch</c>).
/// Null ⇒ the renderer default.</param>
public sealed record SectionLayout(
    SectionLayoutKind Kind,
    FlexDirection Direction = FlexDirection.Row,
    FlexWrap Wrap = FlexWrap.Wrap,
    int Columns = 2,
    int Gap = 4,
    string? CollapseBelow = null,
    string? Density = null,
    string? Align = null);

/// <summary>
/// Per-field placement within a laid-out <see cref="FormSection"/> (ADR 0055
/// Rev 6), keyed by field name on <see cref="FormSection.FieldPlacement"/>.
/// </summary>
/// <param name="ColSpan">Grid-only: how many column tracks the field spans (1–4).</param>
/// <param name="Grow">Flex-only: the <c>flex-grow</c> factor (0 = don't grow).</param>
/// <param name="Width">F-23 flex-only width-fraction intent — one of
/// <see cref="LayoutIntents.Widths"/> (<c>auto</c>/<c>1/4</c>/<c>1/3</c>/<c>1/2</c>/<c>2/3</c>/
/// <c>3/4</c>/<c>full</c>; a bounded token, never pixels). Null ⇒ natural size.</param>
/// <param name="Align">F-23 per-item cross-axis alignment override — one of
/// <see cref="LayoutIntents.Aligns"/>. Null ⇒ inherit the container's alignment.</param>
public sealed record FieldPlacement(
    int ColSpan = 1,
    int Grow = 0,
    string? Width = null,
    string? Align = null);

/// <summary>
/// The closed F-23 layout-intent token vocabularies (breakpoint / density / align / width).
/// Fail-closed: <see cref="FormDefinitionValidation"/> rejects a token outside its set with a
/// stable <c>form.layout.*</c> code at admission — intents are bounded and analyzable, never
/// free-form (and never pixel positioning). Mirrors the TS literal unions in
/// <c>@harborline-software/api-contracts/forms</c> (<c>LayoutBreakpoint</c>/<c>LayoutDensity</c>/
/// <c>LayoutAlign</c>/<c>FieldWidth</c>).
/// </summary>
public static class LayoutIntents
{
    /// <summary>Container-relative collapse breakpoints (small ≈ 24rem, medium ≈ 28rem, large ≈ 32rem).</summary>
    public static readonly IReadOnlySet<string> Breakpoints = new HashSet<string>(StringComparer.Ordinal)
    {
        "sm", "md", "lg",
    };

    /// <summary>Vertical-rhythm densities.</summary>
    public static readonly IReadOnlySet<string> Densities = new HashSet<string>(StringComparer.Ordinal)
    {
        "comfortable", "compact",
    };

    /// <summary>Cross-axis alignment intents.</summary>
    public static readonly IReadOnlySet<string> Aligns = new HashSet<string>(StringComparer.Ordinal)
    {
        "start", "center", "end", "stretch",
    };

    /// <summary>Width-fraction intents for flex items.</summary>
    public static readonly IReadOnlySet<string> Widths = new HashSet<string>(StringComparer.Ordinal)
    {
        "auto", "1/4", "1/3", "1/2", "2/3", "3/4", "full",
    };
}

/// <summary>
/// Read / write authorization for a <see cref="FormSection"/> (ADR 0055
/// §"Section-based permissions").
/// </summary>
/// <remarks>
/// <para>
/// Roles are opaque strings interpreted by the consumer's authorization
/// substrate (typically the macaroon scope vocabulary; for example
/// <c>tenant:admin</c>, <c>vendor:maintenance</c>, <c>collaborator:invited</c>).
/// The keystone substrate does not bind to a specific role taxonomy —
/// authorization enforcement lives in the form engine layered on top.
/// </para>
/// <para>
/// An empty <see cref="ReadRoles"/> list means "no role can read this
/// section" (closed by default). An empty <see cref="WriteRoles"/> means
/// the section is read-only for everyone. The
/// <see cref="ReadConditionExpression"/> is an optional Tier-1
/// (JSON Schema if/then/else) conditional that further narrows read
/// authorization beyond the role check ("vendors with role
/// <c>vendor:maintenance</c> can read this section only when
/// <c>status === 'open'</c>").
/// </para>
/// </remarks>
/// <param name="ReadRoles">Opaque role tokens authorized to read this section.</param>
/// <param name="WriteRoles">Opaque role tokens authorized to write this section.</param>
/// <param name="ReadConditionExpression">Optional Tier-1 conditional further
/// narrowing read authorization; the rule evaluator (forthcoming) interprets
/// this against the entity instance.</param>
/// <param name="ReadStandings">Optional record-derived facts admitted separately from roles.</param>
/// <param name="WriteStandings">Optional record-derived facts admitted separately from roles.</param>
public sealed record SectionAccess(
    IReadOnlyList<string> ReadRoles,
    IReadOnlyList<string> WriteRoles,
    string? ReadConditionExpression = null,
    IReadOnlyList<RecordStandingReference>? ReadStandings = null,
    IReadOnlyList<RecordStandingReference>? WriteStandings = null);
