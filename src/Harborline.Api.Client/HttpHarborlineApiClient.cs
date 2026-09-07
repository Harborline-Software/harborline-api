using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Contracts;

namespace Harborline.Api.Client;

public sealed class HttpHarborlineApiClient(HttpClient httpClient) : IHarborlineApiClient, IHarborlineRuntimeAdapter
{
    public string AdapterName => "Harborline.Api.Client.Http";
    public HarborlineAdapterSafety Safety => HarborlineAdapterSafety.ProductionCapable;

    public async ValueTask<HarborlineApiResponse> SendAsync(
        HarborlineApiRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Route);
        message.Headers.Add("X-Harborline-Tenant", request.Context.TenantId);
        message.Headers.Add("X-Harborline-User", request.Context.UserId);
        if (request.Body is { } body) message.Content = JsonContent.Create(body);

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        JsonElement? responseBody = response.Content.Headers.ContentLength == 0
            ? null
            : await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var problem = response.IsSuccessStatusCode
            ? null
            : new HarborlineProblem("transport-response", response.ReasonPhrase ?? "Harborline API request failed", (int)response.StatusCode);
        return new HarborlineApiResponse((int)response.StatusCode, responseBody, problem);
    }
}
