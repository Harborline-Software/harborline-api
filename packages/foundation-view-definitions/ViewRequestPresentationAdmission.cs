using System.Text.Json;

namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>Closed, non-executable input and result presentation for generic request actions.</summary>
public static class ViewRequestPresentationAdmission
{
    /// <summary>Validates optional presentation metadata and returns whether a binary input is declared.</summary>
    public static bool Admit(JsonElement action)
    {
        var file = action.TryGetProperty("fileInput", out var fileInput);
        if (file)
        {
            Require(!action.TryGetProperty("input", out _) && !action.TryGetProperty("inputForm", out _));
            Require(SingleText(fileInput, "accept") == "application/octet-stream");
        }
        if (action.TryGetProperty("result", out var result))
            Require(SingleText(result, "refresh") is "none" or "data-source");
        return file;
    }

    private static string? SingleText(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
            || !value.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String) return null;
        return text.GetString();
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
    }
}
