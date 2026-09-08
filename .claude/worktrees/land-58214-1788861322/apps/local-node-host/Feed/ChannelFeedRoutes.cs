using System.Collections.Generic;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>
/// Update-feed U2 — the node-local CHANNEL routes (update-feed design note §5.1). Tenant-scoped + gated on
/// <c>packages:operate</c> exactly like the sibling install routes:
/// <list type="bullet">
///   <item><c>POST /channels/{id}/check</c> — the EXPLICIT user-initiated check (§5.2: an offline-first
///     product never phones home silently; the scheduled/auto-check opt-in is U6). Fetch → verify → stage,
///     then hand each staged verified artifact to the EXISTING <c>installer.Preview</c> (the same method
///     <c>/packs/preview</c> calls — no new install path) so the response shows the update against installed
///     state.</item>
///   <item><c>GET /channels</c> — the channel table (config rows + whether each is locally pinned; F6 —
///     an un-pinned row is a "proposed channel"). U4 adds full CRUD + the add-root ceremony.</item>
/// </list>
/// </summary>
public static class ChannelFeedRoutes
{
    /// <summary>Route template: explicit check of one channel.</summary>
    public const string CheckRoute = "/api/local-node/channels/{id}/check";

    /// <summary>Route: list the channel table.</summary>
    public const string ListChannelsRoute = "/api/local-node/channels";

    /// <summary>Maps the channel routes, closing over the host-resolved dependencies. The
    /// <paramref name="previewTrustStore"/> + <paramref name="previewRevocation"/> back the EXISTING
    /// install-preview engine (they carry the channel publisher root so a staged pack previews clean).</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IChannelFeedClient client,
        IChannelRegistry registry,
        IPackInstaller installer,
        IPackInstallStore store,
        IPackTrustStore previewTrustStore,
        IPackRevocationList previewRevocation,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(previewTrustStore);
        ArgumentNullException.ThrowIfNull(previewRevocation);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // ONE tenant resolution point for both routes (ADR 0160 R3-D keeps this surface at a single
        // process-global active-team read; a second call site would raise the frozen debt inventory).
        TenantId ResolveTenant() => NodeTenant.Resolve(activeTeam);

        // POST /channels/{id}/check — verify + stage + preview. OPERATE-side: `packages:operate`.
        app.MapPost(CheckRoute, async (HttpContext http, string id, CancellationToken ct) =>
        {
            var tenant = ResolveTenant();
            // The act addresses a CHANNEL, not one pack: no pack record target, so the gate admits it only
            // because `packages:operate` is declared install-wide on the definition side.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Operate, null, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var result = await client.CheckAsync(tenant, id, ct).ConfigureAwait(false);

            // Hand each staged verified artifact to the EXISTING install-preview engine (no new install
            // path). Preview never mutates; it re-runs the verify-before-effect gate over the staged canon
            // and computes the update-vs-installed diff the Apps card (U3) renders.
            var packs = new List<ChannelCheckPackDto>(result.StagedPacks.Count);
            foreach (var staged in result.StagedPacks)
            {
                var ctx = new PackInstallContext(
                    tenant, previewTrustStore, previewRevocation, time.GetUtcNow(), ChannelFeedClient.RevocationMaxAge);
                var preview = installer.Preview(staged.ArtifactBytes.Span, ctx);

                // §5.3 derivation: feed latest vs installed (F5) vs the S-8 watermark hits the preview
                // above already computed vs the UF-4 node-wide epoch counter — no new verify, no new
                // version-compare (reuses the SAME preview + PackVersion.Compare).
                var active = store.GetActive(tenant, staged.PackKey);
                var state = PackUpdateStateDeriver.Derive(staged, active, preview);

                // Reuse the SAME wire projection `/packs/preview` returns (PackInstallRoutes.ToPreviewDto —
                // made internal for exactly this reuse) so the FULL graph-diff (NewSeedKeys,
                // CrossPackCollisions, UnmetContentReferences — not just verdict/isUpgrade) rides this
                // response. The Apps card's "Update to vN…" opens the EXISTING InstallPreviewDialog /
                // PackDiffPreview over THIS payload — no second fetch, no forked preview surface.
                packs.Add(new ChannelCheckPackDto(
                    staged.PackKey, staged.Latest, staged.MinNodeEpoch, staged.ArtifactCid.Value,
                    state.ToString(), active?.Version, PackInstallRoutes.ToPreviewDto(preview)));
            }

            logger.LogInformation(
                "Channel CHECK (tenant {Tenant}, channel {Channel}) → {Status}, {Packs} staged pack(s), "
                + "{Failures} failure(s).",
                tenant, id, result.Status, result.StagedPacks.Count, result.Failures.Count);

            var summary = result.Summary is null
                ? null
                : new ChannelSummaryDto(
                    result.Summary.Channel, result.Summary.Sequence, result.Summary.GeneratedAt,
                    result.Summary.ValidUntil, result.Summary.RevokedCount);

            var dto = new ChannelCheckResponseDto(
                result.ChannelId,
                result.Status.ToString(),
                result.Ok,
                summary,
                packs,
                result.Failures.Select(f => new FeedFindingDto(f.Code, f.Detail, f.Path)).ToList(),
                result.Warnings.Select(f => new FeedFindingDto(f.Code, f.Detail, f.Path)).ToList());

            // A clean verify (even with no packs) is 200; a verify/transport failure is 422 (unprocessable,
            // mirroring the install route's refusal shape) so the client renders the honest failed state.
            return result.Ok ? Results.Ok(dto) : Results.UnprocessableEntity(dto);
        });

        // GET /channels — the channel table (config + pinned?). OPERATE-side: `packages:operate`.
        app.MapGet(ListChannelsRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var tenant = ResolveTenant();
            // The channel TABLE is an install-wide read — it names no pack.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Operate, null, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var rows = registry.ListChannels()
                .Select(c =>
                {
                    var pin = registry.GetPin(c.ChannelId);
                    return new ChannelRowDto(
                        c.ChannelId, c.DisplayName, c.FeedBase, c.Kind.ToString(), c.ExpectsRootKeyId,
                        c.Enabled, IsPinned: pin is not null, Grade: pin?.Grade);
                })
                .ToList();
            return Results.Ok(rows);
        });
    }
}

// ── Wire DTOs (local to the routes) ─────────────────────────────────────────────

/// <summary>The channel-check response: overall status + channel facts + per-pack (feed facts + the existing
/// install preview) + fail-closed findings.</summary>
public sealed record ChannelCheckResponseDto(
    string ChannelId,
    string Status,
    bool Ok,
    ChannelSummaryDto? Channel,
    IReadOnlyList<ChannelCheckPackDto> Packs,
    IReadOnlyList<FeedFindingDto> Failures,
    IReadOnlyList<FeedFindingDto> Warnings);

/// <summary>The verified channel-level facts.</summary>
public sealed record ChannelSummaryDto(
    string Channel, long Sequence, DateTimeOffset GeneratedAt, DateTimeOffset ValidUntil, int RevokedCount);

/// <summary>One verified pack in the check: the feed facts + the §5.3 update-state derivation (a stable
/// token — <see cref="PackUpdateState"/> — the client localizes off, never an English literal) + the
/// EXISTING full install-preview graph-diff of its staged artifact (<see cref="PreviewResponseDto"/> — the
/// SAME shape <c>/packs/preview</c> returns, so the Apps card's "Update to vN…" opens the EXISTING
/// InstallPreviewDialog / PackDiffPreview over this payload with no second fetch).</summary>
public sealed record ChannelCheckPackDto(
    string PackKey, string Latest, long MinNodeEpoch, string ArtifactCid,
    string UpdateState, string? InstalledVersion, PreviewResponseDto Preview);

/// <summary>One verification finding (stable code + human detail + feed path) the client localizes off the
/// code.</summary>
public sealed record FeedFindingDto(string Code, string Detail, string? Path);

/// <summary>One channel-table row: config + whether it is locally pinned (F6 — un-pinned ⇒ proposed).</summary>
public sealed record ChannelRowDto(
    string ChannelId, string DisplayName, string FeedBase, string Kind, string ExpectsRootKeyId,
    bool Enabled, bool IsPinned, string? Grade);
