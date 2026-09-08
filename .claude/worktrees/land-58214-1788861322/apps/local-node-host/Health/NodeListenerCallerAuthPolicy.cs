using Microsoft.AspNetCore.Routing.Patterns;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The single listener-level pre-authentication allowlist used by both request enforcement and
/// executable endpoint evidence. This policy says only whether the node caller credential is
/// required; it does not describe PBAC or tenant authorization inside a handler.
/// </summary>
internal static class NodeListenerCallerAuthPolicy
{
    internal const string PreCallerAuthAllowlistedPolicy = "pre-caller-auth-allowlisted";
    internal const string CallerCredentialRequiredPolicy = "caller-credential-required-when-enforced";

    private static readonly AllowlistEntry[] Allowlist =
    [
        new("/health", IncludeDescendants: false),
        new("/live", IncludeDescendants: false),
        new("/ready", IncludeDescendants: false),
        new("/ws", IncludeDescendants: true),
        new("/api/session/login", IncludeDescendants: false),
        new("/api/session/antiforgery", IncludeDescendants: false),
        new("/api/session/account-challenge", IncludeDescendants: false),
        new("/api/session/account-setup-accept", IncludeDescendants: false),
        new("/api/session/recovery-accept", IncludeDescendants: false),
        new("/api/session/select", IncludeDescendants: false),
        new("/api/session/logout", IncludeDescendants: false),
    ];

    /// <summary>Returns true when the path matches the reviewed entry-specific allowlist mode.</summary>
    internal static bool IsAllowlisted(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalizedPath = path.Length > 1 && path.EndsWith('/', StringComparison.Ordinal)
            ? path[..^1]
            : path;

        foreach (var entry in Allowlist)
        {
            if (normalizedPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) ||
                (entry.IncludeDescendants &&
                 normalizedPath.StartsWith(entry.Path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the stable evidence token for a route template. A template that can match both a
    /// pre-auth allowlisted path and a caller-authenticated path refuses because one token could
    /// not truthfully describe all requests ASP.NET may dispatch through it.
    /// </summary>
    internal static string DispositionFor(RoutePattern routePattern)
    {
        ArgumentNullException.ThrowIfNull(routePattern);

        var hasParameters = routePattern.Parameters.Count > 0;
        if (!hasParameters)
        {
            return IsAllowlisted(routePattern.RawText ?? string.Empty)
                ? PreCallerAuthAllowlistedPolicy
                : CallerCredentialRequiredPolicy;
        }

        var firstLiteral = ExactLiteralSegment(routePattern, 0);
        if (firstLiteral is null)
        {
            throw MixedPolicyTemplate(routePattern);
        }

        // /ws is the sole subtree allowlist. Any parameterized route whose first segment is
        // exactly "ws" can only produce paths covered by that reviewed entry.
        if (firstLiteral.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            return PreCallerAuthAllowlistedPolicy;
        }

        // Exact-only allowlist entries must not be generalized by templates. Refuse any template
        // whose shape can match one of those exact paths; constraints are intentionally ignored so
        // an opaque/custom constraint can never make the evidence more permissive.
        if (Allowlist.Any(entry =>
                !entry.IncludeDescendants && CanMatchExactPath(routePattern, entry.Path)))
        {
            throw MixedPolicyTemplate(routePattern);
        }

        return CallerCredentialRequiredPolicy;
    }

    private static string? ExactLiteralSegment(RoutePattern pattern, int index)
    {
        if (pattern.PathSegments.Count <= index)
        {
            return null;
        }
        var parts = pattern.PathSegments[index].Parts;
        return parts.Count == 1 && parts[0] is RoutePatternLiteralPart literal
            ? literal.Content
            : null;
    }

    private static bool CanMatchExactPath(RoutePattern pattern, string path)
    {
        var targetSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return CanMatchExactPath(pattern.PathSegments, targetSegments, patternIndex: 0, targetIndex: 0);
    }

    private static bool CanMatchExactPath(
        IReadOnlyList<RoutePatternPathSegment> patternSegments,
        IReadOnlyList<string> targetSegments,
        int patternIndex,
        int targetIndex)
    {
        if (patternIndex == patternSegments.Count)
        {
            return targetIndex == targetSegments.Count;
        }

        var segment = patternSegments[patternIndex];
        if (segment.Parts.Count == 1 && segment.Parts[0] is RoutePatternParameterPart parameter)
        {
            if (parameter.IsCatchAll)
            {
                return true;
            }
            if (targetIndex == targetSegments.Count)
            {
                return (parameter.IsOptional || parameter.Default is not null) && CanMatchExactPath(
                    patternSegments,
                    targetSegments,
                    patternIndex + 1,
                    targetIndex);
            }
        }
        else if (targetIndex < targetSegments.Count &&
                 segment.Parts.Count == 1 &&
                 segment.Parts[0] is RoutePatternLiteralPart literal &&
                 !literal.Content.Equals(targetSegments[targetIndex], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A non-catchall parameter or complex segment consumes one path segment. Complex segments
        // are treated as possible matches rather than interpreting inline/custom policies here.
        return targetIndex < targetSegments.Count && CanMatchExactPath(
            patternSegments,
            targetSegments,
            patternIndex + 1,
            targetIndex + 1);
    }

    private static InvalidOperationException MixedPolicyTemplate(RoutePattern pattern) =>
        new(
            $"Executable route template '{pattern.RawText}' cannot be assigned one listener " +
            "caller-auth policy because it may intersect a pre-auth allowlist boundary.");

    private sealed record AllowlistEntry(string Path, bool IncludeDescendants);
}
