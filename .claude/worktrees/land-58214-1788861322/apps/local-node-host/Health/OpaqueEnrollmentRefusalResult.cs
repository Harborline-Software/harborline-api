using Microsoft.AspNetCore.Http;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The one canonical HTTP refusal for every pairing/mode-exclusive cause. Status, entity bytes, and
/// application-controlled headers are fixed together so a future result-factory change cannot reintroduce a
/// status/header oracle.
/// </summary>
internal sealed class OpaqueEnrollmentRefusalResult : IResult
{
    internal static readonly OpaqueEnrollmentRefusalResult Instance = new();
    private readonly IResult _inner = Results.Json(
        new { error = "enrollment_refused" },
        statusCode: StatusCodes.Status400BadRequest);

    private OpaqueEnrollmentRefusalResult()
    {
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        await _inner.ExecuteAsync(httpContext).ConfigureAwait(false);
    }
}
