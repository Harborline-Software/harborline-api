using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.OrgBranding;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local org-branding surface (tenant-branding slice T1). The node STORES and SERVES the active
/// tenant's <see cref="OrgBrandingProfile"/>:
/// <list type="bullet">
///   <item><c>GET  /api/local-node/org-branding</c> — the resolved branding for the active tenant (the
///     fallback ladder applied; design §2.5). The tenant-lead chrome (T2) + document header (T5) read this.</item>
///   <item><c>GET  /api/local-node/org-branding/logo?variant=light|dark</c> — the raw logo bytes (404 when
///     unset / not locally available — the client falls back to the wordmark; never a broken image).</item>
///   <item><c>PUT  /api/local-node/org-branding</c> — set the org name + optional AA-gated accent. An accent
///     that cannot hit AA is rejected (400) or clamped to the nearest AA-passing tint.</item>
///   <item><c>POST /api/local-node/org-branding/logo?variant=light|dark</c> — upload a raster logo. An
///     oversized / disallowed-type / unreadable asset is rejected (400) at upload — fail-closed.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Server-side tenant (ADR 0091/0092).</b> Every route resolves the active-team-derived tenant via
/// <see cref="NodeTenant.Resolve"/> — the Harborline App sends NO tenant id; the store is tenant-keyed so a
/// foreign tenant never reads another org's branding (cross-tenant isolated).
/// </para>
/// <para>
/// <b>Closed-over deps</b>, NOT <c>[FromServices]</c> — the routes mount on <c>SharedHostedWebApp</c>'s inner
/// <c>WebApplication</c> whose provider lacks the outer registrations (bug-2849). The hosted endpoint takes
/// the store / resolver / blob store / active-team accessor in its ctor and passes them into <see cref="Map"/>.
/// </para>
/// <para>
/// <b>Write authority (design §3.3).</b> Branding is an AP-class, org-wide, owner/admin-scoped action gated
/// on <c>org:branding:write</c> (see <see cref="OrgBrandingAuthority"/>). Route-boundary PBAC on node-loopback
/// routes is a separate platform-wide follow-up (the multi-user-enrollment precondition); until it lands the
/// write records a server-derived <see cref="UpdatedBySentinel"/> for audit (never body-supplied).
/// </para>
/// </remarks>
public static class OrgBrandingRoutes
{
    /// <summary>Canonical route base for the org-branding surface.</summary>
    public const string RouteBase = "/api/local-node/org-branding";

    /// <summary>
    /// The server-derived <c>updatedBy</c> stamp for the single-user loopback node. Per-principal attribution
    /// arrives with route-level node-loopback PBAC (the multi-user-enrollment precondition). It is a SERVER
    /// constant — never taken from the request body (the no-body-supplied-identity audit invariant).
    /// </summary>
    public const string UpdatedBySentinel = "local-operator";

    /// <summary>Maps the org-branding read/serve/write routes onto <paramref name="app"/>.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IOrgBrandingStore store,
        IOrgBrandingResolver resolver,
        IBlobStore blobs,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapGetResolved(app, resolver);
        MapGetLogo(app, store, blobs, activeTeam);
        MapPutProfile(app, store, resolver, activeTeam, timeProvider);
        MapPostLogo(app, store, blobs, activeTeam, timeProvider);
    }

    // ── GET /org-branding — the resolved branding for the active tenant (fallback ladder applied) ─────────
    private static void MapGetResolved(IEndpointRouteBuilder app, IOrgBrandingResolver resolver)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var resolved = await resolver.ResolveActiveAsync(ct).ConfigureAwait(false);
            return Results.Ok(OrgBrandingView.From(resolved));
        });
    }

    // ── GET /org-branding/logo — the raw logo bytes for the requested variant ─────────────────────────────
    private static void MapGetLogo(
        IEndpointRouteBuilder app, IOrgBrandingStore store, IBlobStore blobs, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/logo", async (string? variant, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var profile = await store.GetAsync(tenant, ct).ConfigureAwait(false);
            var reference = profile?.LogoRefFor(ParseVariant(variant));
            if (string.IsNullOrEmpty(reference))
            {
                return Results.NotFound();
            }

            var bytes = await blobs.GetAsync(new Cid(reference), ct).ConfigureAwait(false);
            if (bytes is not { } data)
            {
                // The ref is set but the blob is not locally available — 404 so the client falls back to the
                // wordmark rather than rendering a broken image (the fallback ladder holds at the edge).
                return Results.NotFound();
            }

            // Re-sniff the stored bytes for the content-type (cheap header read; avoids a stored mime column).
            var sniff = LogoAssetValidator.Validate(data.Span);
            var contentType = MimeFor(sniff.Format);
            return Results.File(data.ToArray(), contentType);
        });
    }

    // ── PUT /org-branding — set the org name + optional AA-gated accent ────────────────────────────────────
    private static void MapPutProfile(
        IEndpointRouteBuilder app, IOrgBrandingStore store, IOrgBrandingResolver resolver,
        IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPut(RouteBase, async (OrgBrandingWriteBody body, CancellationToken ct) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "missing-body" });
            }

            var tenant = NodeTenant.Resolve(activeTeam);
            var existing = await store.GetAsync(tenant, ct).ConfigureAwait(false);

            string? accentColor = null;
            string? accentForeground = null;
            var accentClamped = false;

            var requestedAccent = string.IsNullOrWhiteSpace(body.AccentColor) ? null : body.AccentColor!.Trim();
            if (requestedAccent is not null)
            {
                var resolution = AccentAaDeriver.Resolve(requestedAccent);
                if (!resolution.Accepted)
                {
                    // An accent that cannot hit AA is rejected with a plain reason (design §2.4).
                    return Results.BadRequest(new { error = "accent-not-aa", reason = resolution.RejectionReason });
                }

                accentColor = resolution.ResolvedAccent;
                accentForeground = resolution.Foreground;
                accentClamped = resolution.WasClamped;
            }

            var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName!.Trim();

            var profile = new OrgBrandingProfile(
                TenantId: tenant.Value,
                DisplayName: displayName,
                LogoRef: existing?.LogoRef,          // preserve — logo is set via the upload route
                LogoDarkRef: existing?.LogoDarkRef,
                AccentColor: accentColor,
                AccentForeground: accentForeground,
                UpdatedAt: timeProvider.GetUtcNow(),
                UpdatedBy: UpdatedBySentinel);

            await store.UpsertAsync(profile, ct).ConfigureAwait(false);

            var resolved = await resolver.ResolveActiveAsync(ct).ConfigureAwait(false);
            return Results.Ok(new OrgBrandingWriteResponse(
                Branding: OrgBrandingView.From(resolved),
                AccentClamped: accentClamped));
        });
    }

    // ── POST /org-branding/logo — upload a raster logo (validated, fail-closed) ────────────────────────────
    private static void MapPostLogo(
        IEndpointRouteBuilder app, IOrgBrandingStore store, IBlobStore blobs,
        IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/logo", async (HttpRequest request, string? variant, CancellationToken ct) =>
        {
            // Bounded read: never buffer more than the cap + 1 sentinel byte (fail-closed on oversize).
            var (bytes, overflowed) = await ReadBoundedAsync(
                request.Body, OrgBrandingDefaults.MaxLogoBytes, ct).ConfigureAwait(false);
            if (overflowed)
            {
                return Results.BadRequest(new { error = "invalid-logo", reason = "exceeds-max-size" });
            }

            var validation = LogoAssetValidator.Validate(bytes);
            if (!validation.Ok)
            {
                return Results.BadRequest(new { error = "invalid-logo", reason = validation.RejectionReason });
            }

            // Store the validated bytes on the content-addressed blob substrate + pin so GC never reclaims it.
            var cid = await blobs.PutAsync(bytes, ct).ConfigureAwait(false);
            await blobs.PinAsync(cid, ct).ConfigureAwait(false);

            var tenant = NodeTenant.Resolve(activeTeam);
            var existing = await store.GetAsync(tenant, ct).ConfigureAwait(false);
            var target = ParseVariant(variant);

            var profile = new OrgBrandingProfile(
                TenantId: tenant.Value,
                DisplayName: existing?.DisplayName,
                LogoRef: target == LogoVariant.Light ? cid.Value : existing?.LogoRef,
                LogoDarkRef: target == LogoVariant.Dark ? cid.Value : existing?.LogoDarkRef,
                AccentColor: existing?.AccentColor,
                AccentForeground: existing?.AccentForeground,
                UpdatedAt: timeProvider.GetUtcNow(),
                UpdatedBy: UpdatedBySentinel);

            await store.UpsertAsync(profile, ct).ConfigureAwait(false);

            return Results.Ok(new LogoUploadResponse(
                Ok: true,
                Variant: target == LogoVariant.Dark ? "dark" : "light",
                Format: validation.Format!,
                Width: validation.Width,
                Height: validation.Height,
                Cid: cid.Value));
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static LogoVariant ParseVariant(string? variant) =>
        string.Equals(variant, "dark", StringComparison.OrdinalIgnoreCase) ? LogoVariant.Dark : LogoVariant.Light;

    private static string MimeFor(string? format) => format switch
    {
        "png" => "image/png",
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Read at most <paramref name="maxBytes"/> from <paramref name="body"/>. Returns the bytes and an
    /// <c>overflowed</c> flag set when the stream carried MORE than the cap (the caller rejects fail-closed).
    /// </summary>
    private static async Task<(byte[] Bytes, bool Overflowed)> ReadBoundedAsync(
        Stream body, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                // Copy just enough to exceed the cap by one, then stop — we never buffer an unbounded body.
                return (ms.ToArray(), true);
            }

            ms.Write(buffer, 0, read);
        }

        return (ms.ToArray(), false);
    }
}

// ── Wire shapes (camelCase JSON) ─────────────────────────────────────────────────────────────────────────

/// <summary>The PUT request body: set the org name and/or the optional single accent.</summary>
public sealed record OrgBrandingWriteBody(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("accentColor")] string? AccentColor);

/// <summary>The resolved branding a tenant-lead slot renders (the fallback ladder applied).</summary>
public sealed record OrgBrandingView(
    [property: JsonPropertyName("hasOrgIdentity")] bool HasOrgIdentity,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("logoSource")] string LogoSource,
    [property: JsonPropertyName("hasLightLogo")] bool HasLightLogo,
    [property: JsonPropertyName("hasDarkLogo")] bool HasDarkLogo,
    [property: JsonPropertyName("accentColor")] string? AccentColor,
    [property: JsonPropertyName("accentForeground")] string? AccentForeground)
{
    /// <summary>Projects the resolver result onto the wire shape (the ladder source as a lower-camel string).</summary>
    public static OrgBrandingView From(ResolvedOrgBranding r) => new(
        HasOrgIdentity: r.HasOrgIdentity,
        DisplayName: r.DisplayName,
        LogoSource: r.LogoSource switch
        {
            BrandingLogoSource.OrgLogo => "orgLogo",
            BrandingLogoSource.OrgNameWordmark => "orgNameWordmark",
            _ => "platformDefault",
        },
        HasLightLogo: r.HasLightLogo,
        HasDarkLogo: r.HasDarkLogo,
        AccentColor: r.AccentColor,
        AccentForeground: r.AccentForeground);
}

/// <summary>The PUT response: the newly-resolved branding + whether the accent was clamped to hit AA.</summary>
public sealed record OrgBrandingWriteResponse(
    [property: JsonPropertyName("branding")] OrgBrandingView Branding,
    [property: JsonPropertyName("accentClamped")] bool AccentClamped);

/// <summary>The logo-upload response: the sniffed format + intrinsic dimensions + the stored blob CID.</summary>
public sealed record LogoUploadResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("cid")] string Cid);
