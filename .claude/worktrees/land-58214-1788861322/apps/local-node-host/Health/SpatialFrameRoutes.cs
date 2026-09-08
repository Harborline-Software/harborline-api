using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Authorization;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Read-only node-local SpatialFrameDescriptor resolution surface (ADR 0168 D2; Harborline card
/// G4.1). Lets a client resolve <c>(anchor, frameCode[, frameEpoch])</c> to the frame-defining
/// fields — axisConvention / originDescription / lengthUnit / georeference — so asset-local values
/// are interpretable outside the node process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only and tenant-scoped, never node-level existence</b> (0168 D2-A5). Every read resolves
/// the active-team tenant via <c>NodeTenant.Resolve(activeTeam)</c>; the store's tenant guard means
/// another tenant's frames are simply absent (an opaque 404 / empty list — no cross-tenant
/// existence signal). Minting stays a substrate act (<c>ISpatialFrameDescriptorStore.MintAsync</c>);
/// no write is mapped here.
/// </para>
/// <para>
/// <b>Reads go through the package store, never raw rows.</b> The two governed PII-class cells
/// (originDescription, georeference) are sealed at the storage boundary under the tenant DEK
/// (CP-4, D2-A8); <see cref="ISpatialFrameDescriptorStore"/> is the seam that unseals them for an
/// authorized caller. Both routes gate on <see cref="Permission.SpatialRead"/> resolved per
/// request from the inner PEP — the doctype-scoped read permission minted by card 3777, held by
/// the <c>admin</c> and <c>member</c> compositions and DELIBERATELY NOT by <c>viewer</c> (CIC
/// ruling [2026-08-06]: the read-only floor never sees site coordinates). This closes the
/// tightening seam the first cut left open, when the coarse
/// <see cref="TeamRolePermissions.RecordsRead"/> (granted to Viewer) was the tightest honest
/// constant available.
/// </para>
/// <para>
/// <b>The attestation is deliberately NOT projected.</b> The persistence envelope's signed fields
/// stay row-side: the transport envelope is defined when the D2-A4(c) verification deferral
/// expires, not before (0168 OQ-1 amendment). Quarantine content is likewise NOT exposed here —
/// the CP-4 authority-gated retrieval surface governs any exposure of losing mints.
/// </para>
/// <para>
/// <b>Wiring (bug-2849).</b> Dependencies are injected from the OUTER host container and closed
/// over by <see cref="Map"/> — never resolved via <c>[FromServices]</c> inside the handlers.
/// </para>
/// </remarks>
public static class SpatialFrameRoutes
{
    /// <summary>Canonical route base for the node-local spatial-frame resolution surface.</summary>
    public const string RouteBase = "/api/local-node/spatial-frames";

    /// <summary>Maps the read-only spatial-frame routes, closing over the store + active-team accessor.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ISpatialFrameDescriptorStore frames,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(activeTeam);

        // ── GET {base}/{anchor}/{frameCode} — every epoch of the frame series, ascending ──
        app.MapGet($"{RouteBase}/{{anchor}}/{{frameCode}}",
            async (string anchor, string frameCode, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.SpatialRead, RouteRecord.Of(anchor), ct) is { } denied)
                return denied;
            if (ResolvePrincipalContext(http) is not { } readContext)
                return PrincipalUnresolved();

            // [A14]: normalize ONCE, fail-closed 400 on garbage, and echo the NORMALIZED form —
            // the envelope must never carry a second spelling of the frame identity.
            string normalizedCode;
            try { normalizedCode = SpatialFrameCodes.Normalize(frameCode); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_frame_code" }); }

            var series = await frames
                .ListAsync(tenant, new RegistryEntityId(anchor), normalizedCode, readContext, ct)
                .ConfigureAwait(false);
            return Results.Ok(new FrameSeriesResponse(
                anchor, normalizedCode, series.Select(ToWire).ToArray()));
        });

        // ── GET {base}/{anchor}/{frameCode}/{frameEpoch} — one descriptor by its triple + epoch ──
        app.MapGet($"{RouteBase}/{{anchor}}/{{frameCode}}/{{frameEpoch}}",
            async (string anchor, string frameCode, string frameEpoch, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.SpatialRead, RouteRecord.Of(anchor), ct) is { } denied)
                return denied;
            if (ResolvePrincipalContext(http) is not { } readContext)
                return PrincipalUnresolved();

            if (!long.TryParse(frameEpoch, out var epoch) || epoch < 1)
                return Results.BadRequest(new { error = "invalid_frame_epoch" });

            string normalizedCode;
            try { normalizedCode = SpatialFrameCodes.Normalize(frameCode); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_frame_code" }); }

            var descriptor = await frames
                .FindAsync(tenant, new RegistryEntityId(anchor), normalizedCode, epoch, readContext, ct)
                .ConfigureAwait(false);
            // Absent under this tenant is an opaque 404 — a cross-tenant triple looks identical
            // to a triple that never existed (0168 D2-A5: no node-level existence signal).
            return descriptor is null ? Results.NotFound() : Results.Ok(ToWire(descriptor));
        });
    }

    /// <summary>
    /// Builds the PRIVILEGED read context for a spatial:read-authorized request. The Audit@Read
    /// actorRef MUST be the server-derived request principal (the same validated token instance
    /// the permission check evaluated — ADR 0091 same-token invariant), carried with the
    /// "principal:" scheme. NEVER a device id: a static node id would attribute every unsealing
    /// to the machine and make the audit trail useless for who-saw-what. Returns null when no
    /// principal is resolvable in the request scope — the caller must FAIL CLOSED (no principal,
    /// no PII read), never fall back to a node identity.
    /// </summary>
    private static SpatialFrameReadContext? ResolvePrincipalContext(HttpContext http)
    {
        // Selected-session plane: the request-scoped facade's UserId is the bound principal's
        // canonical party (the same validated principal the permission check evaluated).
        var userId = http.RequestServices.GetService<ICurrentUser>()?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Desktop plane (bootstrap bearer / operator): the facade is unbound, and the
            // permission check that just passed resolved the OPERATOR's grants — so the operator
            // is the acting principal. Their OS-user id is a user principal, never a device id.
            userId = http.RequestServices
                .GetService<ActiveTeamAuthorizationContext>()?.UserId;
        }
        return string.IsNullOrWhiteSpace(userId)
            ? null
            : SpatialFrameReadContext.PrivilegedUnseal($"principal:{userId}");
    }

    /// <summary>Fail-closed 403: authorized permission but no resolvable principal to attribute
    /// the Audit@Read row to — the read is withheld rather than mis-attributed.</summary>
    private static IResult PrincipalUnresolved() => Results.Json(
        new { code = "authorization.principal_unresolved" },
        statusCode: StatusCodes.Status403Forbidden);

    private static FrameWire ToWire(SpatialFrameDescriptor d) => new(
        Anchor: d.Anchor.Value,
        FrameCode: d.FrameCode,
        FrameEpoch: d.FrameEpoch,
        AxisConvention: d.AxisConvention,
        OriginDescription: d.OriginDescription,
        LengthUnit: d.LengthUnit,
        Georeference: d.Georeference is { } g
            ? new GeoreferenceWire(g.ObservedAt, g.GeodeticCrs, g.OriginPosition, g.Orientation, g.PoseBasis)
            : null);

    // ── Response DTOs (camelCase wire) ────────────────────────────────────────────────
    /// <summary>Every epoch of one frame series under the active tenant (ascending epoch).</summary>
    public sealed record FrameSeriesResponse(string Anchor, string FrameCode, FrameWire[] Frames);

    /// <summary>One frame descriptor's defining fields. The mint attestation is deliberately
    /// absent — the transport envelope for signed fields is a D2-A4(c) follow-up.</summary>
    public sealed record FrameWire(
        string Anchor,
        string FrameCode,
        long FrameEpoch,
        string AxisConvention,
        string? OriginDescription,
        string LengthUnit,
        GeoreferenceWire? Georeference);

    /// <summary>The optional timestamped geodetic observation of the frame origin (never a
    /// standing conversion — ADR 0168 D2).</summary>
    public sealed record GeoreferenceWire(
        string ObservedAt,
        string GeodeticCrs,
        IReadOnlyList<double> OriginPosition,
        IReadOnlyList<double>? Orientation,
        string? PoseBasis);
}
