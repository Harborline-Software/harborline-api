using System.Collections.Concurrent;
using System.Text.Json;
using Harborline.Api.Contracts;

namespace Harborline.Api.Testing;

public sealed class FixtureHarborlineApiClient : IHarborlineApiClient, IHarborlineRuntimeAdapter
{
    private readonly ConcurrentDictionary<(string Tenant, string Route), JsonElement> _documents = new();

    public string AdapterName => "Harborline.Api.Testing.Fixture";
    public HarborlineAdapterSafety Safety => HarborlineAdapterSafety.DevelopmentOnly;

    public ValueTask<HarborlineApiResponse> SendAsync(
        HarborlineApiRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var key = (request.Context.TenantId, request.Route);
        if (string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase) && request.Body is { } body)
        {
            _documents[key] = body.Clone();
            return ValueTask.FromResult(new HarborlineApiResponse(200, body.Clone(), null));
        }
        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && _documents.TryGetValue(key, out var document))
            return ValueTask.FromResult(new HarborlineApiResponse(200, document.Clone(), null));

        return ValueTask.FromResult(new HarborlineApiResponse(404, null, new HarborlineProblem("not-found", "Not found", 404)));
    }
}
