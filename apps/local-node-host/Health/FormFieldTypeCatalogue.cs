using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Resolves the server-owned PII default for one authored form field type.</summary>
public interface IFormFieldTypeCatalogue
{
    /// <summary>Returns the registered default, or false when the type is not in the catalogue.</summary>
    bool TryResolvePiiDefault(string fieldType, out PiiSensitivity sensitivity);
}

/// <summary>The built-in form field catalogue used by the node authoring route.</summary>
public sealed class FormFieldTypeCatalogue : IFormFieldTypeCatalogue
{
    /// <summary>The shared immutable built-in catalogue.</summary>
    public static FormFieldTypeCatalogue Shared { get; } = new();

    private static readonly IReadOnlyDictionary<string, PiiSensitivity> Defaults =
        new Dictionary<string, PiiSensitivity>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = PiiSensitivity.None,
            ["textarea"] = PiiSensitivity.None,
            ["number"] = PiiSensitivity.None,
            ["date"] = PiiSensitivity.None,
            ["select"] = PiiSensitivity.None,
            ["radio"] = PiiSensitivity.None,
            ["checkbox"] = PiiSensitivity.None,
            ["currency"] = PiiSensitivity.None,
            ["email"] = PiiSensitivity.None,
            ["datetime"] = PiiSensitivity.None,
            ["multiselect"] = PiiSensitivity.None,
            ["file"] = PiiSensitivity.None,
            ["percentage"] = PiiSensitivity.None,
            ["time"] = PiiSensitivity.None,
            ["boolean-toggle"] = PiiSensitivity.None,
            ["phone"] = PiiSensitivity.None,
            ["url"] = PiiSensitivity.None,
            ["signature"] = PiiSensitivity.None,
            ["address-composite"] = PiiSensitivity.None,
            ["readonly"] = PiiSensitivity.None,
            ["hidden"] = PiiSensitivity.None,
            ["condition-rating"] = PiiSensitivity.None,
        };

    /// <inheritdoc />
    public bool TryResolvePiiDefault(string fieldType, out PiiSensitivity sensitivity) =>
        Defaults.TryGetValue(fieldType ?? string.Empty, out sensitivity);
}

internal sealed class FormFieldProtectionAdmissionException(
    string code,
    string field,
    string offendingValue) : Exception(code)
{
    internal string Code { get; } = code;
    internal string Field { get; } = field;
    internal string OffendingValue { get; } = offendingValue;
}
