using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// SESSION-GATED reverse proxy for Pilot's LLM traffic: forwards <c>/api/llm/v1/*</c> (the
/// OpenAI-compatible surface Pilot uses) to the node's configured upstream (e.g. a co-located
/// Ollama at <c>127.0.0.1:11434</c>). Same-origin from the browser (no CORS surface); localhost
/// from the node. The web client bakes <c>VITE_PILOT_LLM_URL=/api/llm/v1</c>, so this is how a
/// browser Pilot reaches the model without exposing the LLM port or opening CORS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded surface.</b> Only paths under <c>v1/</c> are forwarded (Pilot's OpenAI-compatible
/// calls: <c>v1/chat/completions</c>, <c>v1/models</c>). Anything else 404s — the fixed upstream
/// cannot be used as a general relay. The upstream is a fixed config value (no SSRF: the caller
/// never chooses the host). The route is NOT allowlisted, so the listener caller-auth gate requires
/// a valid session (a logged-in browser user) before a request ever reaches here.
/// </para>
/// <para>
/// <b>Streaming.</b> Uses <c>ResponseHeadersRead</c> + a direct body copy with response buffering
/// disabled, so SSE token deltas stream through to the browser as the model produces them.
/// </para>
/// </remarks>
public static class LlmProxyRoutes
{
    /// <summary>Route base for the LLM proxy.</summary>
    public const string RouteBase = "/api/llm";

    /// <summary>Maps the proxy onto <paramref name="app"/>, closed over the shared client + upstream base.</summary>
    public static void Map(IEndpointRouteBuilder app, HttpClient client, string upstreamBase, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamBase);

        var upstream = upstreamBase.TrimEnd('/');

        app.Map($"{RouteBase}/{{**rest}}", async (HttpContext ctx, string rest) =>
        {
            // Bound to the OpenAI-compatible /v1 surface — the fixed upstream is never a general relay.
            if (rest != "v1" && !rest.StartsWith("v1/", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var target = $"{upstream}/{rest}{ctx.Request.QueryString}";
            using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

            if (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                {
                    upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
                }
            }

            var accept = ctx.Request.Headers.Accept.ToString();
            if (!string.IsNullOrEmpty(accept))
            {
                upstreamReq.Headers.TryAddWithoutValidation("Accept", accept);
            }

            HttpResponseMessage upstreamResp;
            try
            {
                upstreamResp = await client
                    .SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "LLM proxy: upstream unreachable ({Target}).", target);
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsJsonAsync(new { error = "llm_upstream_unreachable" }, ctx.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            using (upstreamResp)
            {
                ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                if (upstreamResp.Content.Headers.ContentType is { } contentType)
                {
                    ctx.Response.ContentType = contentType.ToString();
                }
                // Stream the (possibly SSE) body straight through — no buffering.
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await upstreamResp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            }
        });
    }
}
