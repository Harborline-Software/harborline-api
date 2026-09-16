using Microsoft.AspNetCore.Http;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>A host-validated correlation, distinct from a signing nonce or an authorization fact.</summary>
internal sealed record SelectedRequestCorrelation(Guid Value)
{
    internal const string Header = "X-Correlation-ID";
    internal static IResult? Bind(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(Header, out var values)) return null;
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var value) || value == Guid.Empty)
            return Results.BadRequest(new { code = "request.correlation_invalid" });
        http.Features.Set(new SelectedRequestCorrelation(value));
        return null;
    }
}
