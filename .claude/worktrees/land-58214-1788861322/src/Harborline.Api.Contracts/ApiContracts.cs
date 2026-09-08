using System.Text.Json;

namespace Harborline.Api.Contracts;

public sealed record HarborlineRequestContext(string TenantId, string UserId)
{
    public HarborlineRequestContext Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) throw new ArgumentException("Tenant id is required.", nameof(TenantId));
        if (string.IsNullOrWhiteSpace(UserId)) throw new ArgumentException("User id is required.", nameof(UserId));
        return this;
    }
}

public sealed record HarborlineApiRequest(
    string Method,
    string Route,
    HarborlineRequestContext Context,
    JsonElement? Body = null)
{
    public HarborlineApiRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Method)) throw new ArgumentException("HTTP method is required.", nameof(Method));
        if (string.IsNullOrWhiteSpace(Route) || !Route.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Route must be an absolute application path.", nameof(Route));
        Context.Validate();
        return this;
    }
}

public sealed record HarborlineProblem(string Code, string Title, int Status, string? TraceId = null);

public sealed record HarborlineApiResponse(int Status, JsonElement? Body, HarborlineProblem? Problem)
{
    public bool IsSuccess => Status is >= 200 and < 300 && Problem is null;
}

public interface IHarborlineApiClient
{
    ValueTask<HarborlineApiResponse> SendAsync(HarborlineApiRequest request, CancellationToken cancellationToken = default);
}

public enum HarborlineAdapterSafety
{
    ProductionCapable,
    DevelopmentOnly,
}

public interface IHarborlineRuntimeAdapter
{
    string AdapterName { get; }
    HarborlineAdapterSafety Safety { get; }
}

public static class HarborlineRuntimeGuard
{
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
