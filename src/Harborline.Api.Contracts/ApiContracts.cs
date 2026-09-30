using System.Text.Json;

namespace Harborline.Api.Contracts;

/// <summary>Identifies the tenant and user on whose behalf an API request runs.</summary>
/// <param name="TenantId">A tenant identifier containing at least one non-whitespace character.</param><param name="UserId">A user identifier containing at least one non-whitespace character.</param>
public sealed record HarborlineRequestContext(string TenantId, string UserId)
{
    /// <summary>Validates both identity components and returns this context.</summary>
    public HarborlineRequestContext Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) throw new ArgumentException("Tenant id is required.", nameof(TenantId));
        if (string.IsNullOrWhiteSpace(UserId)) throw new ArgumentException("User id is required.", nameof(UserId));
        return this;
    }
}

/// <summary>Describes an API operation, its caller, and its optional JSON body.</summary>
/// <param name="Method">An HTTP method containing at least one non-whitespace character.</param><param name="Route">An application path beginning with '/'.</param><param name="Context">The caller identity context.</param><param name="Body">The optional request payload.</param>
public sealed record HarborlineApiRequest(
    string Method,
    string Route,
    HarborlineRequestContext Context,
    JsonElement? Body = null)
{
    /// <summary>Validates the method, route, and caller context.</summary>
    public HarborlineApiRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Method)) throw new ArgumentException("HTTP method is required.", nameof(Method));
        if (string.IsNullOrWhiteSpace(Route) || !Route.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Route must be an absolute application path.", nameof(Route));
        Context.Validate();
        return this;
    }
}

/// <summary>Represents a structured API error with an HTTP status and optional trace identifier.</summary>
/// <param name="Code">The stable machine-readable error code.</param><param name="Title">The human-readable error title.</param><param name="Status">The HTTP status code.</param><param name="TraceId">The optional diagnostic trace identifier.</param>
public sealed record HarborlineProblem(string Code, string Title, int Status, string? TraceId = null);

/// <summary>Contains an API status, optional JSON body, and optional problem details.</summary>
/// <param name="Status">The HTTP status code.</param><param name="Body">The optional response payload.</param><param name="Problem">The optional structured error.</param>
public sealed record HarborlineApiResponse(int Status, JsonElement? Body, HarborlineProblem? Problem)
{
    /// <summary>Gets whether the status is successful and no problem was supplied.</summary>
    public bool IsSuccess => Status is >= 200 and < 300 && Problem is null;
}

/// <summary>Sends validated Harborline API requests and returns transport-neutral responses.</summary>
public interface IHarborlineApiClient
{
    /// <summary>Sends a request asynchronously, honoring cancellation when provided.</summary>
    ValueTask<HarborlineApiResponse> SendAsync(HarborlineApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>States whether a runtime adapter is safe for production use.</summary>
public enum HarborlineAdapterSafety
{
    /// <summary>The adapter is approved for production environments.</summary>
    ProductionCapable,
    /// <summary>The adapter is intended only for development environments.</summary>
    DevelopmentOnly,
}

/// <summary>Exposes the identity and deployment safety classification of a runtime adapter.</summary>
public interface IHarborlineRuntimeAdapter
{
    /// <summary>Gets the stable adapter name used in diagnostics.</summary>
    string AdapterName { get; }
    /// <summary>Gets the deployment environments in which this adapter may run.</summary>
    HarborlineAdapterSafety Safety { get; }
}

/// <summary>Rejects development-only adapters when the environment is Production.</summary>
public static class HarborlineRuntimeGuard
{
    /// <summary>Throws when production contains one or more development-only adapters.</summary>
    public static void EnsureEnvironmentAllows(string environmentName, IEnumerable<IHarborlineRuntimeAdapter> adapters)
    {
        if (!string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)) return;

        var prohibited = adapters.Where(adapter => adapter.Safety == HarborlineAdapterSafety.DevelopmentOnly)
            .Select(adapter => adapter.AdapterName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (prohibited.Length > 0)
            throw new InvalidOperationException($"Development-only Harborline adapters are registered in Production: {string.Join(", ", prohibited)}");
    }
}
