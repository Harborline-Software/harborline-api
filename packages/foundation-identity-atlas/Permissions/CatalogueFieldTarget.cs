using System.Text;
namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>One canonical catalogue field target. Vocabulary admission belongs to the typed reader.</summary>
public sealed record CatalogueFieldTarget(string Kind, string Id, string Version, string Field)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public ScopeExpression Scope => ScopeExpression.Parse(CanonicalPath);

    private string CanonicalPath
    {
        get
        {
            if (Field is not ("formId" or "title" or "version" or "cascadeLayer")
                || Version.Split('.') is not { Length: 3 } parts
                || !parts.All(part => part.Length > 0 && (part.Length == 1 || part[0] != '0')
                    && part.All(char.IsAsciiDigit) && int.TryParse(part, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException("Not a supported immutable catalogue field coordinate.");
            return $"{RecordScope(Id).Value}/catalogue-fields/1/{Encode(Kind)}/{Encode(Version)}/{Encode(Field)}";
        }
    }

    public static ScopeExpression RecordScope(string id) => ScopeExpression.Parse($"/records/{Encode(id)}");

    internal static void ValidateRecordScope(string scope)
    {
        var parts = scope.Split('/');
        if (parts.Length != 3 || parts[0] != "" || parts[1] != "records"
            || RecordScope(Decode(parts[2])).Value != scope)
            throw new ArgumentException("Not a canonical catalogue record target.", nameof(scope));
    }

    public static CatalogueFieldTarget Parse(string target)
    {
        var parts = target.Split('/');
        if (parts.Length != 8 || parts[0] != "" || parts[1] != "records"
            || parts[3] != "catalogue-fields" || parts[4] != "1")
            throw new ArgumentException("Not a canonical catalogue field target.", nameof(target));
        var result = new CatalogueFieldTarget(Decode(parts[5]), Decode(parts[2]), Decode(parts[6]), Decode(parts[7]));
        if (!string.Equals(result.CanonicalPath, target, StringComparison.Ordinal))
            throw new ArgumentException("Not a canonical catalogue field target.", nameof(target));
        return result;
    }

    private static string Decode(string value)
    {
        var bytes = new List<byte>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length
                && byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var escaped))
            { bytes.Add(escaped); index += 2; }
            else if (value[index] <= 127 && value[index] != '%') bytes.Add((byte)value[index]);
            else throw new ArgumentException("Invalid encoded component.", nameof(value));
        }
        return Utf8.GetString(bytes.ToArray());
    }

    private static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Any(char.IsControl))
            throw new ArgumentException("Invalid catalogue target component.", nameof(value));
        var result = new StringBuilder();
        foreach (var octet in Utf8.GetBytes(value))
            if (octet is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
                result.Append((char)octet);
            else result.Append('%').Append(octet.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
    }
}
