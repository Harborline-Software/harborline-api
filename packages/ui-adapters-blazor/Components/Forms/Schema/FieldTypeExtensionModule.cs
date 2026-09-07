namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// THE substitutable build-time extension module — the Blazor mirror of the React
/// Harborline App's <c>src/forms/builder/fieldTypeExtensions.ts</c>
/// (ADR 0055 Rev 10 D-R10.2). Upstream content is the EMPTY list. A downstream
/// (partner) build substitutes this ONE file at build time — file-level source
/// substitution in its own bundle — patching no core file and registering nothing at
/// runtime (runtime registration is forbidden by ADR 0154 D6). Each entry is a
/// row+control PAIR (<see cref="FieldTypeExtension"/>), so ADR 0168's "the registry row
/// lands with the control, never before" holds structurally; ADR 0168 D4's spatial field
/// types (which default to <see cref="PiiSensitivityValues.Sensitive"/>) land here.
/// </summary>
public static class FieldTypeExtensionModule
{
    /// <summary>The build-time field-type extensions. Empty upstream, by design.</summary>
    public static IReadOnlyList<FieldTypeExtension> Extensions { get; } =
        Array.Empty<FieldTypeExtension>();
}
