using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Harborline.Kernel.WorkItems;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Admits a command envelope before dispatch to a local-node route.</summary>
internal static class NodeCommandRequests
{
    // An envelope is { "commands": [body] }; each body targets the requested route.
    // Ordinary route bodies retain their existing shape.
    internal static void Use(IApplicationBuilder app)
    {
        app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            var request = http.Request;
            if (!request.Path.StartsWithSegments("/api/local-node", StringComparison.OrdinalIgnoreCase) || !request.HasJsonContentType()
                || !(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
                    || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method)))
            {
                await next(http).ConfigureAwait(false);
                return;
            }

            request.EnableBuffering();
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(request.Body, cancellationToken: http.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                request.Body.Position = 0;
                await next(http).ConfigureAwait(false);
                return;
            }

            using (document)
            {
                request.Body.Position = 0;
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("commands", out var commands))
                {
                    await next(http).ConfigureAwait(false);
                    return;
                }

                if (commands.ValueKind != JsonValueKind.Array || root.EnumerateObject().Count() != 1)
                {
                    await Results.BadRequest(new { code = "request.invalid-command-envelope" })
                        .ExecuteAsync(http).ConfigureAwait(false);
                    return;
                }

                var result = await CommandRequestBoundary.ExecuteAsync<JsonElement, bool>(
                    commands.EnumerateArray().ToArray(), async command =>
                    {
                        var originalBody = request.Body;
                        var originalLength = request.ContentLength;
                        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(command.GetRawText()));
                        request.Body = body;
                        request.ContentLength = body.Length;
                        try
                        {
                            await next(http).ConfigureAwait(false);
                            return true;
                        }
                        finally
                        {
                            request.Body = originalBody;
                            request.ContentLength = originalLength;
                        }
                    }).ConfigureAwait(false);
                if (result.Refusal is { } refusal)
                    await Results.Json(refusal, statusCode: refusal.StatusCode).ExecuteAsync(http).ConfigureAwait(false);
                else if (result.Results.Count == 0)
                    http.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        });
    }
}
