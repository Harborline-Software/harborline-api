using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Health;

internal static class CallerTenantIdentifierFence
{
    internal static void Use(IApplicationBuilder app)
    {
        app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            var request = http.Request;
            if (request.Query.Keys.Any(IsTenantKey)
                || request.Headers.Keys.Any(IsTenantKey)
                || request.RouteValues.Keys.Any(IsTenantKey)
                || await BodyNamesTenantAsync(request, http.RequestAborted).ConfigureAwait(false))
            {
                await Results.BadRequest(new { code = "request.tenant-id-not-accepted" })
                    .ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            await next(http).ConfigureAwait(false);
        });
    }

    private static bool IsTenantKey(string key)
    {
        var normalized = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized is "tenant" or "tenantid" or "tenantidentifier" or "xtenant" or "xtenantid";
    }

    private static async Task<bool> BodyNamesTenantAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            request.EnableBuffering();
            try
            {
                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                return form.Keys.Any(IsTenantKey) || form.Files.Any(file => IsTenantKey(file.Name));
            }
            catch (InvalidDataException)
            {
                throw new BadHttpRequestException("Malformed form body.");
            }
        }

        if (!request.HasJsonContentType()) return false;
        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
            var selection = HttpMethods.IsPost(request.Method)
                && (request.Path.Equals(TenantSelectionRoutes.SelectPath, StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals(TenantSwitchRoutes.SwitchPath, StringComparison.OrdinalIgnoreCase));
            return ContainsTenantKey(document.RootElement, selection);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Malformed JSON body.");
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static bool ContainsTenantKey(JsonElement element, bool selectionRoot = false)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property =>
                (IsTenantKey(property.Name)
                    && !(selectionRoot && property.Name.Equals("tenantId", StringComparison.OrdinalIgnoreCase)))
                || ContainsTenantKey(property.Value));
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsTenantKey(item));
        return false;
    }
}
