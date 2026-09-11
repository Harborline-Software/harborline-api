using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Pack Composer B-1b — the node-local INSTALL routes (design §5 / §6). Tenant-scoped + audited:
/// <c>POST /packs/install</c> (verify → atomic seed layer, Draft), <c>POST /packs/preview</c> (the D8
/// "who signed / what changes / conflicts / watermark hits" preview, no mutation), <c>POST /packs/activate</c>
/// (Draft/Inactive → Active), <c>POST /packs/deactivate</c> (Active → Inactive), and
/// <c>GET /packs/installed</c> (list). Trust is own-roster + Harborline channel only
/// (no third-party); the trust store + revocation list are host-built and closed over here.
/// </summary>
internal static class PackInstallRoutes
{
    /// <summary>Route: verify + install a pack file into an immutable seed layer (Draft).</summary>
    public const string InstallRoute = "/api/local-node/packs/install";

    /// <summary>Route: preview an install (no mutation) — the D8 moment-of-trust surface.</summary>
    public const string PreviewRoute = "/api/local-node/packs/preview";

    /// <summary>Route: activate an installed version (Draft/Inactive → Active).</summary>
    public const string ActivateRoute = "/api/local-node/packs/activate";

    /// <summary>Route: reversibly deactivate an Active installed version (no seed or tenant-data deletion).</summary>
    public const string DeactivateRoute = "/api/local-node/packs/deactivate";

    /// <summary>Route: list installed pack versions for the tenant.</summary>
    public const string ListInstalledRoute = "/api/local-node/packs/installed";

    /// <summary>The revocation-list staleness horizon surfaced in previews/installs.</summary>
    public static readonly TimeSpan RevocationMaxAge = TimeSpan.FromDays(30);

    /// <summary>Maps the install routes, closing over the host-resolved dependencies (bug-2849 — the routes
    /// map onto the shared inner <c>WebApplication</c>). <paramref name="platform"/> is optional (back-compat
    /// embedders): when present, <c>GET /packs/installed</c> marks an Active-but-platform-refused pack so an
    /// operator can SEE a ticket-160 refusal on the list surface, not only in logs.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IPackInstaller installer,
        IPackInstallStore store,
        IPackTrustStore trustStore,
        IPackRevocationList revocation,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider time,
        ILogger logger,
        string authorizingPrincipal,
        IPackSeedProjector projector,
        IPackPlatformCompatibility? platform = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(projector);
        if (installer is not IPackProjectionReconciler reconciler)
            throw new InvalidOperationException("The composed pack installer does not expose its internal projection seam.");
        reconciler.AttachProjector(projector);
        // F3: the break-glass authorizing principal is bound to the node's AUTHENTICATED identity, resolved
        // server-side by the composition root — NEVER a value the HTTP caller can assert.
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizingPrincipal);

        // The family splits at this funnel: mutations are selected-session product operations,
        // while the installed-pack read is already part of the LAN data-route allowlist.
        var selectedSession = app.MapSelectedSessionProductGroup();
        var deviceReachable = app.MapDeviceReachableProductDataGroup();

        // POST /packs/preview — verify + plan; NEVER mutates. OPERATE-side (council A-1): `packages:operate`.
        selectedSession.MapPost(PreviewRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // Preview NEVER mutates and the uploaded artifact is not yet a pack record — no record target,
            // admitted only because `packages:operate` is declared install-wide.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Operate, null, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var bytes = await ReadBodyAsync(http.Request, ct).ConfigureAwait(false);
            var context = new PackInstallContext(tenant, trustStore, revocation, time.GetUtcNow(), RevocationMaxAge);

            var preview = installer.Preview(bytes, context);
            logger.LogInformation(
                "Pack PREVIEW (tenant {Tenant}, pack {Key} v{Version}) → {Verdict}.",
                tenant, preview.PackKey, preview.Version, preview.Verdict);
            return Results.Ok(ToPreviewDto(preview));
        });

        // POST /packs/install — verify → atomic seed layer (Draft). Optional break-glass via query.
        // OPERATE-side (council A-1): `packages:operate`.
        selectedSession.MapPost(InstallRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var httpRequest = http.Request;
            var tenant = NodeTenant.Resolve(activeTeam);
            var bytes = await ReadBodyAsync(httpRequest, ct).ConfigureAwait(false);

            // Install MUTATES, so it must resolve against the pack it is about to write. The pack key is in
            // the artifact, so the route reads it through the EXISTING no-effect preview (the same call
            // `/packs/preview` makes) BEFORE deciding, and only then installs. An artifact whose verify
            // fails names no pack; that request is install-wide and the installer refuses it on its own.
            var naming = installer.Preview(
                bytes, new PackInstallContext(tenant, trustStore, revocation, time.GetUtcNow(), RevocationMaxAge));
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, authority, PackOperation.Operate, naming.PackKey, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            // Break-glass: the operator's JUSTIFICATION is legitimately caller-supplied (it is their stated
            // reason), but the AUTHORIZING PRINCIPAL is bound to the node's authenticated identity resolved
            // server-side — a client can no longer self-assert who authorized the override (F3). A blank
            // justification simply means "no ceremony requested"; a present one mints a validated BreakGlass.
            BreakGlass? breakGlass = null;
            var justification = httpRequest.Query["breakGlassJustification"].ToString();
            if (!string.IsNullOrWhiteSpace(justification))
            {
                breakGlass = new BreakGlass(justification, authorizingPrincipal);
            }

            // Ticket 151: the installer REQUIRES the acting principal. Ticket 379: it is the principal
            // THIS request was just decided about — `authority.Principal`, the one the guard above
            // resolved — and NOT `authorizingPrincipal`, which is the node's own signing key id (the
            // break-glass authorizer). The installer re-resolves the same act at the gate, so naming the
            // key id there asked about an actor no grant is ever issued to and every first-boot install
            // was refused as a 500 (m3 exit run, 2026-09-10).
            var context = new PackInstallContext(
                tenant, trustStore, revocation, time.GetUtcNow(), RevocationMaxAge, breakGlass,
                Principal: authority.Principal.Value);
            var outcome = installer.Install(bytes, context);

            logger.LogInformation(
                "Pack INSTALL (tenant {Tenant}, pack {Key} v{Version}) → installed={Installed} action={Action} "
                + "breakGlass={BrokeGlass} [{Codes}].",
                tenant, outcome.PackKey, outcome.Version, outcome.Installed, outcome.Action, outcome.BrokeGlass,
                string.Join(",", outcome.RefusalCodes));

            var dto = new InstallResponseDto(
                Installed: outcome.Installed,
                Action: outcome.Action.ToString(),
                PackKey: outcome.PackKey,
                Version: outcome.Version,
                BrokeGlass: outcome.BrokeGlass,
                RefusalCodes: outcome.RefusalCodes,
                Preview: ToPreviewDto(outcome.Preview));

            return outcome.Installed ? Results.Ok(dto) : Results.UnprocessableEntity(dto);
        });

        // POST /packs/activate — Draft/Inactive → Active. OPERATE-side (council A-1): `packages:operate`.
        selectedSession.MapPost(ActivateRoute, async (HttpContext http, ActivatePackRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PackKey) || string.IsNullOrWhiteSpace(request.Version))
            {
                return Results.BadRequest(new { error = "packKey and version are required." });
            }

            var tenant = NodeTenant.Resolve(activeTeam);
            // The pointer flip is an act ON the named pack — that pack is the record target.
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, authority, PackOperation.Operate, request.PackKey, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            // Ticket 151 cluster: the pointer flip carries the same server-derived acting principal
            // the install path does (never a client-asserted value) — the principal this request was
            // decided about, not the node's signing key id (ticket 379).
            var context = new PackInstallContext(
                tenant, trustStore, revocation, time.GetUtcNow(), RevocationMaxAge,
                Principal: authority.Principal.Value,
                OwnershipResolutions: (request.Resolutions ?? Array.Empty<CollisionResolutionDto>())
                    .Where(r => !string.IsNullOrWhiteSpace(r.ContentKey) && !string.IsNullOrWhiteSpace(r.OwningPackKey))
                    .ToDictionary(r => r.ContentKey, r => r.OwningPackKey, StringComparer.Ordinal));
            var outcome = installer.Activate(context, request.PackKey, request.Version);
            logger.LogInformation(
                "Pack ACTIVATE (tenant {Tenant}, pack {Key} v{Version}) → activated={Activated} [{Error}].",
                tenant, request.PackKey, request.Version, outcome.Activated, outcome.Error);

            if (!outcome.Activated)
            {
                return Results.UnprocessableEntity(new { activated = false, error = outcome.Error, detail = outcome.Detail });
            }

            // Draft→Active is when a pack's declarative content becomes live — project its seed layer into
            // the runtime registries the read APIs consume (asset types → the Type Manager dropdown), so an
            // installed+activated pack actually populates the surface instead of only appearing in the pack
            // list. Idempotent + additive-kind-safe; a projection hiccup must not un-activate the pack, so
            // failures are logged, not propagated.
            IReadOnlyList<ProjectionRefusalDto> projectionRefusals = Array.Empty<ProjectionRefusalDto>();
            IReadOnlyList<PlatformProjectionRefusalDto> platformRefusals =
                Array.Empty<PlatformProjectionRefusalDto>();
            if (outcome.ProjectionResult is PackSeedProjectionSummary projection)
            {
                projectionRefusals = projection.Refusals
                    .Select(r => new ProjectionRefusalDto(r.ContentKey, r.ContentKind.ToString(), r.Code))
                    .ToList();
                platformRefusals = ToPlatformRefusalDtos(projection);
                logger.LogInformation(
                    "Pack ACTIVATE projection (tenant {Tenant}, pack {Key} v{Version}) → {Seeded} asset type(s) "
                    + "seeded, {Present} already present, {FormsPublished} form(s) published, "
                    + "{FormsPresent} already present, {FormsInvalid} invalid, {FormsDeferred} deferred, "
                    + "{WorkflowsPublished} workflow(s) published, {WorkflowsPresent} already present, "
                    + "{WorkflowsInvalid} refused, {WorkflowsDeferred} deferred.",
                    tenant, request.PackKey, request.Version,
                    projection.AssetTypesSeeded, projection.AssetTypesAlreadyPresent,
                    projection.FormDefinitionsPublished, projection.FormDefinitionsAlreadyPresent,
                    projection.FormDefinitionsSkippedInvalid, projection.FormDefinitionsDeferred,
                    projection.WorkflowDefinitionsPublished, projection.WorkflowDefinitionsAlreadyPresent,
                    projection.WorkflowDefinitionsSkippedInvalid, projection.WorkflowDefinitionsDeferred);
            }
            else if (!outcome.Projected)
            {
                logger.LogError(
                    "Pack ACTIVATE projection FAILED (tenant {Tenant}, pack {Key} v{Version}) — durable "
                    + "admission evidence remains pending for startup reconciliation. {Detail}",
                    tenant, request.PackKey, request.Version, outcome.Detail);
            }

            await Task.CompletedTask.ConfigureAwait(false);

            return Results.Ok(new ActivatePackResponseDto(
                true, outcome.PackKey, outcome.Version, projectionRefusals, platformRefusals));
        });

        // POST /packs/deactivate — Active → Inactive plus reversible runtime retraction. No purge, no seed
        // deletion, no authored-revision deletion, and no tenant-record deletion.
        // OPERATE-side (council A-1): `packages:operate`.
        selectedSession.MapPost(DeactivateRoute, async (HttpContext http, DeactivatePackRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PackKey) || string.IsNullOrWhiteSpace(request.Version))
            {
                return Results.BadRequest(new { error = "packKey and version are required." });
            }

            var tenant = NodeTenant.Resolve(activeTeam);
            // Retraction is an act ON the named pack — that pack is the record target.
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, authority, PackOperation.Operate, request.PackKey, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var context = new PackInstallContext(
                tenant, trustStore, revocation, time.GetUtcNow(), RevocationMaxAge,
                Principal: authority.Principal.Value);
            var outcome = installer.Deactivate(context, request.PackKey, request.Version);
            logger.LogInformation(
                "Pack DEACTIVATE (tenant {Tenant}, pack {Key} v{Version}) → deactivated={Deactivated} [{Error}].",
                tenant, request.PackKey, request.Version, outcome.Deactivated, outcome.Error);

            if (!outcome.Deactivated)
            {
                return Results.UnprocessableEntity(new
                {
                    deactivated = false,
                    packKey = outcome.PackKey,
                    version = outcome.Version,
                    error = outcome.Error,
                });
            }

            IReadOnlyList<ProjectionRefusalDto> projectionRefusals = Array.Empty<ProjectionRefusalDto>();
            IReadOnlyList<PlatformProjectionRefusalDto> platformRefusals =
                Array.Empty<PlatformProjectionRefusalDto>();
            if (outcome.ProjectionResult is PackSeedProjectionSummary projection)
            {
                projectionRefusals = projection.Refusals
                    .Select(r => new ProjectionRefusalDto(r.ContentKey, r.ContentKind.ToString(), r.Code))
                    .ToList();
                platformRefusals = ToPlatformRefusalDtos(projection);
                logger.LogInformation(
                    "Pack DEACTIVATE retraction (tenant {Tenant}, pack {Key} v{Version}) → "
                    + "{Assets} asset type(s), {Forms} form(s), and {Workflows} workflow(s) retracted.",
                    tenant, request.PackKey, request.Version,
                    projection.AssetTypesRetracted,
                    projection.FormDefinitionsRetracted,
                    projection.WorkflowDefinitionsRetracted);
            }
            else if (!outcome.Projected)
            {
                logger.LogError(
                    outcome.ProjectionResult as Exception,
                    "Pack DEACTIVATE retraction FAILED (tenant {Tenant}, pack {Key} v{Version}) — "
                    + "durable admission evidence remains pending for startup reconciliation.",
                    tenant, request.PackKey, request.Version);
            }

            await Task.CompletedTask.ConfigureAwait(false);

            return Results.Ok(new DeactivatePackResponseDto(
                true, outcome.PackKey, outcome.Version, projectionRefusals, platformRefusals));
        });

        // GET /packs/installed — list installed versions for the tenant. OPERATE-side: `packages:operate`.
        deviceReachable.MapGet(ListInstalledRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // The installed-pack LIST is an install-wide read — it names no one pack.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Operate, null, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var installed = store.ListInstalled(tenant)
                .Select(p => new InstalledPackDto(
                    p.PackKey, p.Version, p.Lifecycle.ToString(), p.InstalledAtUtc,
                    p.SignerKeyId.ToBase64Url(), p.Epoch, p.VouchingScope.ToString(),
                    // Ticket 160 visibility: an Active pack the projector refuses on platform
                    // compatibility looks healthy in this list otherwise — mark it. Null when the
                    // host runs without platform facts (the marker is then unknowable, not false).
                    PlatformRefused: platform is null
                        ? null
                        : p.Lifecycle == PackLifecycleState.Active
                            && PackPlatformRequirementCheck.FindUnmet(p, platform).Count > 0))
                .ToList();
            return Results.Ok(installed);
        });
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Projects the pass's pack-grain platform refusals (ticket 160) onto the wire — the
    /// activate/deactivate responses would otherwise silently DROP a sibling pack's refusal.</summary>
    private static IReadOnlyList<PlatformProjectionRefusalDto> ToPlatformRefusalDtos(
        Data.PackProjection.PackSeedProjectionSummary projection)
        => projection.PlatformRefusals
            .Select(r => new PlatformProjectionRefusalDto(
                r.PackKey,
                r.Version,
                r.PlatformVersion,
                Data.PackProjection.PackPlatformProjectionRefusal.Code,
                r.Unmet
                    .Select(u => new UnmetPlatformRequirementDto(
                        u.Capability, u.MinimumPlatformVersion, u.DeclaredBy, u.Failure.ToString()))
                    .ToList()))
            .ToList();

    /// <summary>Projects the install engine's <see cref="PackInstallPreview"/> to the wire DTO. Made
    /// <c>internal</c> (not <c>private</c>) so the update-feed U3 channel-check route (same assembly) reuses
    /// this SINGLE projection for the graph-diff the Apps card opens via "Update to vN…" — no second,
    /// drifting copy of the mapping.</summary>
    internal static PreviewResponseDto ToPreviewDto(PackInstallPreview p) => new(
        Verdict: p.Verdict.ToString(),
        PackKey: p.PackKey,
        Version: p.Version,
        SignerKeyId: p.SignerKeyId,
        Epoch: p.Epoch,
        VouchingScope: p.VouchingScope?.ToString(),
        IsUpgrade: p.IsUpgrade,
        PriorVersion: p.PriorVersion,
        NewSeedKeys: p.NewSeedKeys,
        Conflicts: p.Conflicts.Select(c => new ReattachConflictDto(
            c.ContentKey, c.ContentKind.ToString(), c.Kind.ToString(), c.Path, c.SeedValueJson, c.OverlayValueJson)).ToList(),
        WatermarkHits: p.WatermarkHits.Select(h => new WatermarkHitDto(h.Kind.ToString(), h.Detail)).ToList(),
        AdmissionRefusals: p.AdmissionRefusals.Select(a => new AdmissionRefusalDto(a.ContentKey, a.Code, a.Message)).ToList(),
        RevocationStale: p.RevocationStale,
        RefusalCodes: p.RefusalCodes,
        CrossPackCollisions: p.CrossPackCollisions.Select(c => new CrossPackCollisionDto(
            c.ContentKey, c.ContentKind.ToString(), c.ClaimingPackKeys, c.Resolution.ToString(), c.OwnerPackKey)).ToList(),
        UnmetContentReferences: p.UnmetContentReferences.Select(u => new UnmetContentReferenceDto(
            u.FromContentKey, u.ToPackKey, u.ToContentKey, u.Relation.ToString())).ToList(),
        UnmetDependencies: p.UnmetDependencies.Select(d => new UnmetDependencyDto(
            d.DependencyKey, d.PinnedVersion, d.InstalledVersion, d.MalformedPin)).ToList());
}

// ── Wire DTOs (local to the routes) ─────────────────────────────────────────────

/// <summary>The activate request body. <see cref="Resolutions"/> optionally records explicit owning-pack
/// choices for cross-pack same-key collisions BEFORE the Draft/Inactive → Active flip, so the activation guard +
/// the seed projector resolve those keys to the chosen owner instead of refusing (F4).</summary>
public sealed record ActivatePackRequestDto(
    string PackKey, string Version, IReadOnlyList<CollisionResolutionDto>? Resolutions = null);

/// <summary>The reversible deactivate request. The explicit version prevents a stale client from disabling a
/// newer Active version than the one whose consequences it reviewed.</summary>
public sealed record DeactivatePackRequestDto(string PackKey, string Version);

/// <summary>The activation result plus any non-mutating projection refusals. Codes are stable localization
/// keys; an item refusal never rolls the pack's successfully activated lifecycle pointer back.
/// <see cref="PlatformRefusals"/> carries the PACK-grain ticket-160 refusals the same pass produced —
/// possibly for a SIBLING active pack, which this activation legitimately surfaces (the operator flipping
/// pointers is exactly who must see an out-of-window pack).</summary>
public sealed record ActivatePackResponseDto(
    bool Activated,
    string PackKey,
    string Version,
    IReadOnlyList<ProjectionRefusalDto> ProjectionRefusals,
    IReadOnlyList<PlatformProjectionRefusalDto>? PlatformRefusals = null);

/// <summary>The successful reversible deactivate response plus any content-grain retraction refusals and
/// pack-grain platform refusals (see <see cref="ActivatePackResponseDto.PlatformRefusals"/>).</summary>
public sealed record DeactivatePackResponseDto(
    bool Deactivated,
    string PackKey,
    string Version,
    IReadOnlyList<ProjectionRefusalDto> ProjectionRefusals,
    IReadOnlyList<PlatformProjectionRefusalDto>? PlatformRefusals = null);

/// <summary>One content item the post-activation projector refused.</summary>
public sealed record ProjectionRefusalDto(string ContentKey, string ContentKind, string Code);

/// <summary>One ACTIVE pack a projection pass refused whole on platform compatibility (ticket 160).</summary>
public sealed record PlatformProjectionRefusalDto(
    string PackKey,
    string Version,
    string PlatformVersion,
    string Code,
    IReadOnlyList<UnmetPlatformRequirementDto> Unmet);

/// <summary>One declared platform requirement the running build cannot satisfy.</summary>
public sealed record UnmetPlatformRequirementDto(
    string Capability, string? MinimumPlatformVersion, string DeclaredBy, string Failure);

/// <summary>One explicit owning-pack choice for a contested content key (D8 per-key ownership).</summary>
public sealed record CollisionResolutionDto(string ContentKey, string OwningPackKey);

/// <summary>The D8 preview response.</summary>
public sealed record PreviewResponseDto(
    string Verdict,
    string PackKey,
    string Version,
    string? SignerKeyId,
    long? Epoch,
    string? VouchingScope,
    bool IsUpgrade,
    string? PriorVersion,
    IReadOnlyList<string> NewSeedKeys,
    IReadOnlyList<ReattachConflictDto> Conflicts,
    IReadOnlyList<WatermarkHitDto> WatermarkHits,
    IReadOnlyList<AdmissionRefusalDto> AdmissionRefusals,
    bool RevocationStale,
    IReadOnlyList<string> RefusalCodes,
    IReadOnlyList<CrossPackCollisionDto> CrossPackCollisions,
    IReadOnlyList<UnmetContentReferenceDto> UnmetContentReferences,
    IReadOnlyList<UnmetDependencyDto> UnmetDependencies);

/// <summary>One manifest-declared dependency whose target pack is not installed (or only below the pin,
/// or pinned with a version string that does not parse — <paramref name="MalformedPin"/>). Stable tokens
/// the client localizes; no English display text.</summary>
public sealed record UnmetDependencyDto(
    string DependencyKey, string PinnedVersion, string? InstalledVersion, bool MalformedPin = false);

/// <summary>One cross-pack same-key collision surfaced in the preview (both pack ids + the shared key +
/// how/whether it resolves to an owner).</summary>
public sealed record CrossPackCollisionDto(
    string ContentKey, string ContentKind, IReadOnlyList<string> ClaimingPackKeys, string Resolution, string? OwnerPackKey);

/// <summary>One declared content-grain cross-app reference (G2) whose target app is NOT installed — the
/// "requires X — not installed" seam. Stable tokens the client localizes; no English display text.</summary>
public sealed record UnmetContentReferenceDto(
    string FromContentKey, string ToPackKey, string ToContentKey, string Relation);

/// <summary>The install response (the outcome + the plan that produced it).</summary>
public sealed record InstallResponseDto(
    bool Installed,
    string Action,
    string PackKey,
    string Version,
    bool BrokeGlass,
    IReadOnlyList<string> RefusalCodes,
    PreviewResponseDto Preview);

/// <summary>One re-attach conflict in the preview.</summary>
public sealed record ReattachConflictDto(
    string ContentKey, string ContentKind, string Kind, string Path, string? SeedValueJson, string? OverlayValueJson);

/// <summary>One S-8 watermark hit in the preview.</summary>
public sealed record WatermarkHitDto(string Kind, string Detail);

/// <summary>One admission refusal in the preview.</summary>
public sealed record AdmissionRefusalDto(string ContentKey, string Code, string Message);

/// <summary>One installed pack version in the list surface. <paramref name="PlatformRefused"/> marks an
/// Active pack the projector currently refuses on platform compatibility (ticket 160) — null when the
/// host runs without platform facts (unknowable, not false).</summary>
public sealed record InstalledPackDto(
    string PackKey, string Version, string Lifecycle, DateTimeOffset InstalledAtUtc,
    string SignerKeyId, long Epoch, string VouchingScope, bool? PlatformRefused = null);
