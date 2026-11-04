using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
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
        return normalized is "tenant" or "tenantid" or "tenantidentifier" or "xtenant" or "xtenantid" or "xtenantidentifier";
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
            string? tenant = null;
            var activeTeam = request.HttpContext.RequestServices.GetService<IActiveTeamAccessor>();
            // This fence precedes listener authentication; only its existing desktop guard can
            // authorize the exception. The route still performs listener and author admission.
            if (HttpMethods.IsPost(request.Method)
                && request.Path.Equals(PackComposerRoutes.ExportRoute, StringComparison.OrdinalIgnoreCase)
                && request.HttpContext.RequestServices.GetService<NodeCallerSessionToken>()?.Validate(request)
                    == NodeCallerSessionToken.Decision.Allow
                && activeTeam?.Active is not null)
            {
                tenant = NodeTenant.Resolve(activeTeam).Value;
            }
            return ContainsTenantKey(document.RootElement, selection, tenant, BodyLocation.Root);
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

    private enum BodyLocation { Other, Root, Contents, Entry, Records, Envelope }

    private static bool ContainsTenantKey(JsonElement element, bool selectionRoot = false,
        string? tenant = null, BodyLocation location = BodyLocation.Other)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property =>
            {
                if (IsTenantKey(property.Name)
                    && !(selectionRoot && property.Name.Equals("tenantId", StringComparison.Ordinal))
                    && !(property.Name == "tenant" && tenant is not null && location == BodyLocation.Envelope
                        && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == tenant))
                    return true;
                var childLocation = (location, property.Name) switch
                {
                    (BodyLocation.Root, "contents") => BodyLocation.Contents,
                    (BodyLocation.Entry, "content") when tenant is not null
                        && element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                        && Enum.TryParse<PackContentKind>(kind.GetString(), true, out var contentKind)
                        && PackContentReferenceExtractor.RecordsId(contentKind, JsonNode.Parse(property.Value.GetRawText())) is not null
                        => BodyLocation.Records,
                    (BodyLocation.Records, "envelope") => BodyLocation.Envelope,
                    _ => BodyLocation.Other,
                };
                return ContainsTenantKey(property.Value, tenant: tenant, location: childLocation);
            });
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsTenantKey(item, tenant: tenant,
                location: location == BodyLocation.Contents ? BodyLocation.Entry : BodyLocation.Other));
        return false;
    }
}
