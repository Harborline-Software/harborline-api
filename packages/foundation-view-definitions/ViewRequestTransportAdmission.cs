using System.Text.RegularExpressions;

namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>Refuses a malformed host registration before its request can enter an active render plan.</summary>
internal static partial class ViewRequestTransportAdmission
{
    internal static bool IsValid(ViewRequestDescriptor descriptor)
    {
        if ((descriptor.RowsPointer is null) != (descriptor.RowIdentityPointer is null)
            || (descriptor.RowsPointer is { } rows && (!Pointer().IsMatch(rows)
                || !Pointer().IsMatch(descriptor.RowIdentityPointer!)))) return false;
        if (descriptor.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")
            || descriptor.ContentType is not ("application/json" or "application/octet-stream")
            || descriptor.RouteTemplate is not { Length: > 0 } route || !route.StartsWith('/', StringComparison.Ordinal)
            || route.Contains("//", StringComparison.Ordinal) || route.Contains('\\', StringComparison.Ordinal)
            || route.Contains('?', StringComparison.Ordinal) || route.Contains('#', StringComparison.Ordinal)
            || route.Split('/').Any(segment => segment is "." or "..")
            || (descriptor.Method != "GET" && !descriptor.RequiresAntiforgery)) return false;
        var paths = PathTokens().Matches(route).Select(match => match.Groups[1].Value).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length
            || PathTokens().Replace(route, "").IndexOfAny(['{', '}']) >= 0) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var wires = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundPaths = new HashSet<string>(StringComparer.Ordinal);
        var roots = 0;
        var fields = 0;
        foreach (var input in descriptor.Inputs)
        {
            if (!Name().IsMatch(input.Name) || !names.Add(input.Name) || !Enum.IsDefined(input.Kind)
                || input.Placement is not { } placement || !Enum.IsDefined(placement) || input.WireName is not { } wire
                || !wires.Add($"{placement}:{wire}")) return false;
            switch (placement)
            {
                case ViewRequestPlacement.Path:
                    if (input.Kind != ViewRequestValueKind.Text || !paths.Contains(wire, StringComparer.Ordinal)) return false;
                    boundPaths.Add(wire);
                    break;
                case ViewRequestPlacement.Header:
                    // Credential, antiforgery and forwarding headers are owned by the transport, never a descriptor input.
                    if (input.Kind != ViewRequestValueKind.Text || wire is not ("Idempotency-Key" or "X-Correlation-ID")) return false;
                    break;
                case ViewRequestPlacement.BodyRoot:
                    if (wire.Length != 0 || ++roots > 1
                        || (descriptor.ContentType == "application/json" ? input.Kind != ViewRequestValueKind.Object
                            : input.Kind != ViewRequestValueKind.Binary)) return false;
                    break;
                case ViewRequestPlacement.BodyField:
                    if (!Name().IsMatch(wire) || input.Kind == ViewRequestValueKind.Binary) return false;
                    fields++;
                    break;
                default: return false;
            }
        }
        return boundPaths.Count == paths.Length && !(roots > 0 && fields > 0)
            && (descriptor.Method != "GET" || (roots == 0 && fields == 0 && descriptor.ContentType == "application/json"))
            && (descriptor.ContentType != "application/octet-stream" || roots == 1);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9_-]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PathTokens();
    [GeneratedRegex("^/[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pointer();
}
