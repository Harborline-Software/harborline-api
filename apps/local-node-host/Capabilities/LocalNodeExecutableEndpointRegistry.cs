using System.Collections.Immutable;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Capabilities;

/// <summary>One normalized route from the node's actual ASP.NET endpoint graph.</summary>
internal sealed record LocalNodeExecutableEndpoint(
    string RoutePattern,
    ImmutableArray<string> HttpMethods,
    string ListenerCallerAuthPolicy,
    ImmutableArray<LocalNodeExecutableMethodRouteFence> MethodRouteFences);

/// <summary>Explicit route-fence evidence for one executable HTTP method and route pair.</summary>
internal sealed record LocalNodeExecutableMethodRouteFence(
    string HttpMethod,
    RouteFenceKind? RouteFenceKind);

/// <summary>
/// Immutable technical evidence captured from the active node profile immediately before Kestrel
/// binds. It is not a capability claim and does not prove PBAC or tenant isolation.
/// </summary>
internal sealed record LocalNodeExecutableEndpointSnapshot(
    string SchemaVersion,
    string Producer,
    bool ListenerCallerAuthEnforced,
    bool PublicStaticFilesEnabled,
    ImmutableArray<LocalNodeExecutableEndpoint> Endpoints)
{
    internal const string SupportedSchemaVersion = "shipyard.local-node-executable-registry/v0";
    internal const string ProducerId = "shipyard.local-node-host";
}

/// <summary>
/// Seal-once registry derived from the endpoint data sources that ASP.NET will actually execute.
/// It deliberately refuses unknown endpoint kinds and overlapping method-pattern registrations.
/// </summary>
public sealed class LocalNodeExecutableEndpointRegistry
{
    private static readonly ImmutableArray<RouteFenceExpectation> RouteFenceExpectations =
    [
        new(FormsRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        new(CommsRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        new(FounderBindRoutes.BindPath, RouteFenceKind.DesktopPlaneOnly),
        new(AdmissionRoutes.RouteBase, RouteFenceKind.FounderWebAdmission),
        new(CurrentPrincipalSignatureRoutes.Route, RouteFenceKind.DesktopPlaneOnly),
        new(PackComposerRoutes.ExportRoute, RouteFenceKind.DesktopPlaneOnly),
        new(PackComposeRoutes.ComposeRoute, RouteFenceKind.DesktopPlaneOnly),
        // Keyed on the DEFINITIONS base, not /api/local-node/reports: the report-RUN routes under
        // the parent base are DeviceReachableProductData, and widening this expectation to the
        // parent would fail their seal (ticket 085).
        new(ReportDefinitionRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        new(ViewDefinitionRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        new(DataExchangeDefinitionRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        new(AuthorizationAdminRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
        // Ticket 213: recording who consented to what is an administrative act over the install's own
        // records, and it is never a LAN device's to perform -- the same audience as the authorization
        // admin surface, not the device-reachable product-data families.
        new(ConsentRecordRoutes.RouteBase, RouteFenceKind.DesktopPlaneOnly),
    ];

    private readonly object _gate = new();
    private LocalNodeExecutableEndpointSnapshot? _current;

    /// <summary>True after the active endpoint graph has been captured exactly once.</summary>
    internal bool IsSealed => Volatile.Read(ref _current) is not null;

    /// <summary>The immutable active-profile snapshot. Access before sealing fails closed.</summary>
    internal LocalNodeExecutableEndpointSnapshot Current =>
        Volatile.Read(ref _current) ??
        throw new InvalidOperationException("The local-node executable endpoint registry is not sealed.");

    /// <summary>Captures and normalizes the actual endpoint graph.</summary>
    internal void Seal(
        IEnumerable<EndpointDataSource> sources,
        bool listenerCallerAuthEnforced,
        bool publicStaticFilesEnabled)
    {
        ArgumentNullException.ThrowIfNull(sources);

        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException(
                    "The local-node executable endpoint registry has already been sealed.");
            }

            var routes = new Dictionary<string, RouteBucket>(StringComparer.Ordinal);

            foreach (var endpoint in sources.SelectMany(source => source.Endpoints))
            {
                if (endpoint is not RouteEndpoint routeEndpoint)
                {
                    throw new InvalidOperationException(
                        $"Unsupported executable endpoint type '{endpoint.GetType().FullName}'.");
                }

                var routePattern = routeEndpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(routePattern))
                {
                    throw new InvalidOperationException("An executable route has no raw route pattern.");
                }
                if (routePattern[0] != '/')
                {
                    throw new InvalidOperationException(
                        $"Executable route '{routePattern}' is not rooted and cannot be classified exactly.");
                }

                AssertExpectedRouteFence(routeEndpoint, routePattern);
                var routeFenceKind = routeEndpoint.Metadata.GetMetadata<RouteFenceMetadata>()?.Kind;

                // EndpointMetadataCollection resolves one most-significant item of each metadata
                // type. Reading every item would describe methods ASP.NET does not execute when a
                // later convention overrides an earlier one.
                var methodMetadata = routeEndpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
                if (methodMetadata?.AcceptCorsPreflight is true)
                {
                    throw new InvalidOperationException(
                        $"Executable route '{routePattern}' accepts CORS preflight, which the " +
                        "endpoint evidence profile does not model.");
                }

                var methods = (methodMetadata?.HttpMethods ?? [])
                    .Select(NormalizeMethod)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                if (methods.Length == 0)
                {
                    methods = ["*"];
                }

                var structuralKey = StructuralRouteKey(routeEndpoint.RoutePattern);
                if (!routes.TryGetValue(structuralKey, out var bucket))
                {
                    bucket = new RouteBucket(routePattern, routeEndpoint.RoutePattern);
                    routes.Add(structuralKey, bucket);
                }
                else if (string.CompareOrdinal(routePattern, bucket.CanonicalPattern) < 0)
                {
                    bucket.CanonicalPattern = routePattern;
                    bucket.CanonicalRoutePattern = routeEndpoint.RoutePattern;
                }

                foreach (var method in methods)
                {
                    bucket.Add(method, routeFenceKind);
                }
            }

            var endpoints = routes.Values
                .Select(bucket => new LocalNodeExecutableEndpoint(
                    bucket.CanonicalPattern,
                    bucket.MethodRouteFences.Keys.Order(StringComparer.Ordinal).ToImmutableArray(),
                    NodeListenerCallerAuthPolicy.DispositionFor(bucket.CanonicalRoutePattern),
                    bucket.MethodRouteFences
                        .OrderBy(item => item.Key, StringComparer.Ordinal)
                        .Select(item => new LocalNodeExecutableMethodRouteFence(item.Key, item.Value))
                        .ToImmutableArray()))
                .OrderBy(endpoint => endpoint.RoutePattern, StringComparer.Ordinal)
                .ThenBy(endpoint => string.Join('\n', endpoint.HttpMethods), StringComparer.Ordinal)
                .ToImmutableArray();

            Volatile.Write(
                ref _current,
                new LocalNodeExecutableEndpointSnapshot(
                    LocalNodeExecutableEndpointSnapshot.SupportedSchemaVersion,
                    LocalNodeExecutableEndpointSnapshot.ProducerId,
                    listenerCallerAuthEnforced,
                    publicStaticFilesEnabled,
                    endpoints));
        }
    }

    private static string NormalizeMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method) ||
            !string.Equals(method, method.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An executable endpoint has an invalid HTTP method.");
        }

        return method.ToUpperInvariant();
    }

    private static void AssertExpectedRouteFence(RouteEndpoint endpoint, string routePattern)
    {
        foreach (var expectation in RouteFenceExpectations)
        {
            if (!RoutePatternOverlapsBase(endpoint.RoutePattern, expectation.BasePattern))
            {
                continue;
            }

            var marker = endpoint.Metadata.GetMetadata<RouteFenceMetadata>();
            if (marker?.Kind != expectation.Kind)
            {
                var actual = marker is null ? "none" : marker.Kind.ToString();
                // An endpoint shape the technical inventory cannot represent may degrade without
                // changing request authority. An unfenced consequential route is different: the
                // unchanged app would serve it without its security boundary, so startup must refuse.
                throw new RouteFenceViolationException(
                    $"Executable route '{routePattern}' must carry the " +
                    $"'{expectation.Kind}' route-fence marker; actual marker: '{actual}'.");
            }
        }
    }

    private static bool RoutePatternOverlapsBase(RoutePattern pattern, RoutePattern routeBase)
    {
        for (var index = 0; index < routeBase.PathSegments.Count; index++)
        {
            if (index >= pattern.PathSegments.Count)
            {
                return pattern.PathSegments.Count > 0 &&
                    SegmentContainsCatchAll(pattern.PathSegments[^1]);
            }

            var candidate = pattern.PathSegments[index];
            var expected = routeBase.PathSegments[index];

            if (SegmentContainsCatchAll(candidate))
            {
                return true;
            }

            if (candidate.Parts.Count == 1 &&
                candidate.Parts[0] is RoutePatternLiteralPart candidateLiteral &&
                expected.Parts.Count == 1 &&
                expected.Parts[0] is RoutePatternLiteralPart expectedLiteral)
            {
                if (!candidateLiteral.Content.Equals(
                        expectedLiteral.Content,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            // Parameters (including optional/defaulted parameters) and complex segments can
            // consume the watched base's literal segment. Without proof of disjoint match spaces,
            // the endpoint belongs to the fence.
        }

        return true;
    }

    private static bool SegmentContainsCatchAll(RoutePatternPathSegment segment) =>
        segment.Parts.Any(part =>
            part is RoutePatternParameterPart { IsCatchAll: true });

    private static string StructuralRouteKey(RoutePattern pattern)
    {
        var key = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            key.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        AppendLengthPrefixed(key, 'L', literal.Content.ToUpperInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        AppendLengthPrefixed(key, 'S', separator.Content.ToUpperInvariant());
                        break;
                    case RoutePatternParameterPart parameter:
                        key.Append("P:")
                            .Append(parameter.IsCatchAll ? '1' : '0')
                            .Append(parameter.IsOptional || parameter.Default is not null ? '1' : '0')
                            .Append(':');
                        var policies = parameter.ParameterPolicies
                            .Select(policy => CanonicalPolicy(pattern, policy))
                            .Order(StringComparer.Ordinal)
                            .ToArray();
                        key.Append(policies.Length).Append(':');
                        foreach (var policy in policies)
                        {
                            AppendLengthPrefixed(key, 'C', policy);
                        }
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Executable route '{pattern.RawText}' contains unsupported route part " +
                            $"'{part.GetType().FullName}'.");
                }
            }
        }
        return key.ToString();
    }

    private static string CanonicalPolicy(
        RoutePattern pattern,
        RoutePatternParameterPolicyReference policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Content))
        {
            throw new InvalidOperationException(
                $"Executable route '{pattern.RawText}' has an opaque parameter policy that " +
                "cannot be represented deterministically.");
        }

        var argumentStart = policy.Content.IndexOf('(', StringComparison.Ordinal);
        return argumentStart < 0
            ? policy.Content.ToUpperInvariant()
            : policy.Content[..argumentStart].ToUpperInvariant() + policy.Content[argumentStart..];
    }

    private static void AppendLengthPrefixed(StringBuilder key, char kind, string value) =>
        key.Append(kind).Append(value.Length).Append(':').Append(value);

    private sealed class RouteBucket(string canonicalPattern, RoutePattern canonicalRoutePattern)
    {
        internal string CanonicalPattern { get; set; } = canonicalPattern;
        internal RoutePattern CanonicalRoutePattern { get; set; } = canonicalRoutePattern;
        internal Dictionary<string, RouteFenceKind?> MethodRouteFences { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string method, RouteFenceKind? routeFenceKind)
        {
            if ((method == "*" && MethodRouteFences.Count > 0) ||
                (method != "*" && MethodRouteFences.ContainsKey("*")) ||
                !MethodRouteFences.TryAdd(method, routeFenceKind))
            {
                throw new InvalidOperationException(
                    $"Duplicate or overlapping executable route '{method} {CanonicalPattern}'.");
            }
        }
    }

    private sealed record RouteFenceExpectation(string RouteBase, RouteFenceKind Kind)
    {
        internal RoutePattern BasePattern { get; } = RoutePatternFactory.Parse(RouteBase);
    }
}
