namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>The closed set of presentation roles a field may play inside one view shape.</summary>
public enum ShapeRole
{
    /// <summary>The field rendered as the view item's human-readable title.</summary>
    Title = 0,

    /// <summary>The ordered or temporal field used to place the item in the view.</summary>
    PlacedBy = 1,

    /// <summary>The scalar field used to group items in the view.</summary>
    GroupedBy = 2,
}

/// <summary>
/// Maps presentation roles to record fields for exactly one <see cref="ViewDefinition"/>.
/// This is view-local presentation metadata, not an authorization vocabulary entry.
/// </summary>
/// <param name="Title">The text field rendered as the item title.</param>
/// <param name="PlacedBy">The date/time or ordered field used to place the item.</param>
/// <param name="GroupedBy">The scalar field used to group items.</param>
public sealed record ShapeRoleMapping(
    string? Title = null,
    string? PlacedBy = null,
    string? GroupedBy = null)
{
    internal IEnumerable<(ShapeRole Role, string? Field)> Entries
    {
        get
        {
            yield return (ShapeRole.Title, Title);
            yield return (ShapeRole.PlacedBy, PlacedBy);
            yield return (ShapeRole.GroupedBy, GroupedBy);
        }
    }

    internal bool HasMappings => Entries.Any(entry => entry.Field is not null);
}

/// <summary>The compatibility of one field in the record type targeted by a view.</summary>
public enum ViewRecordFieldKind
{
    /// <summary>A textual scalar.</summary>
    Text = 0,

    /// <summary>A date, time, or date/time scalar.</summary>
    DateTime = 1,

    /// <summary>An ordered scalar such as an integer or decimal number.</summary>
    Ordered = 2,

    /// <summary>Another scalar, such as a boolean.</summary>
    Scalar = 3,

    /// <summary>An object, array, or otherwise non-scalar field.</summary>
    Complex = 4,
}

/// <summary>A descriptor-owned projection of the target record type and its fields.</summary>
/// <param name="RecordType">The stable record-type identifier.</param>
/// <param name="Fields">Fields keyed by their exact record field names.</param>
public sealed record ViewRecordTypeDescriptor(
    string RecordType,
    IReadOnlyDictionary<string, ViewRecordFieldKind> Fields);

internal static class ShapeRoleMappingValidator
{
    internal const string MissingFieldCode = "view_definition.shape_role_field_missing";
    internal const string IncompatibleFieldCode = "view_definition.shape_role_field_incompatible";

    internal static void Validate(ShapeRoleMapping mapping, ViewRecordTypeDescriptor? recordType)
    {
        foreach (var (role, field) in mapping.Entries)
        {
            if (field is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(field)
                || recordType is null
                || !recordType.Fields.TryGetValue(field, out var kind))
            {
                throw new ViewDefinitionGovernanceException(MissingFieldCode);
            }

            if (!IsCompatible(role, kind))
            {
                throw new ViewDefinitionGovernanceException(IncompatibleFieldCode);
            }
        }
    }

    private static bool IsCompatible(ShapeRole role, ViewRecordFieldKind kind) => role switch
    {
        ShapeRole.Title => kind == ViewRecordFieldKind.Text,
        ShapeRole.PlacedBy => kind is ViewRecordFieldKind.DateTime or ViewRecordFieldKind.Ordered,
        ShapeRole.GroupedBy => kind != ViewRecordFieldKind.Complex,
        _ => false,
    };
}
