using Microsoft.AspNetCore.Components;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// The arguments a schema control renderer receives — the Blazor mirror of ui-react's
/// <c>ControlArgs</c> (ADR 0055 Rev 10). Kept renderer-agnostic: <see cref="ValueKind"/>
/// is a bare string the caller synthesises from its field-type registry; the resolver
/// attaches no semantics beyond <c>== "string"</c> (see <see cref="SchemaValueKinds"/>).
/// </summary>
/// <param name="FieldName">The candidate-document property name (also the control's DOM id).</param>
/// <param name="ControlHint">The field's declared renderer-control hint; <see langword="null"/> reads as <c>"text"</c>.</param>
/// <param name="ValueKind">Caller-synthesised value-kind token; <see langword="null"/> reads as string (legacy FormView shape).</param>
/// <param name="StringValue">The field's current value projected to a string (empty when absent).</param>
/// <param name="Disabled">True when the field is read-only per the server-projected rule state.</param>
/// <param name="HasError">True when the field currently carries a validation error.</param>
public sealed record SchemaControlContext(
    string FieldName,
    string? ControlHint,
    string? ValueKind = null,
    string StringValue = "",
    bool Disabled = false,
    bool HasError = false);

/// <summary>
/// A schema control renderer: produces the control's markup for a resolved field.
/// Registered per control hint through <see cref="FieldTypeRegistry.Compose"/> —
/// either as a core control or paired with a <see cref="FieldTypeExtension"/> row.
/// </summary>
public delegate RenderFragment SchemaControlRenderer(SchemaControlContext context);
