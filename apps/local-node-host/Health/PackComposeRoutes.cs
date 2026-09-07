using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Pack Composer B-2a — the guarded COMPOSE ceremony routes (design note 2026-07-06 §3/§4; council fold
/// cerebrum 2026-07-06). Three steps behind the outbound moment of trust, all gated on
/// <c>packages:author</c> at the ROUTE (council A-1 — the UI rail is a reflection, not the enforcement):
/// <list type="number">
/// <item><c>POST /packs/compose</c> — SNAPSHOT-AT-COMPOSE (Q3): freeze the selected authored artifacts into
/// a content-addressed draft + return the exact bytes for the human PII review.</item>
/// <item><c>POST /packs/compose/{id}/affirm</c> — bind the human PII-review affirmation to the snapshot hash
/// SERVER-SIDE (S-1); a hash mismatch is refused.</item>
/// <item><c>POST /packs/compose/{id}/export</c> — re-hash + sign own-roster through the exporter's fail-closed
/// DCP gate (Q1); returns the sneakernet file.</item>
/// </list>
/// A <c>GET /packs/compose/{id}</c> re-reads a draft (so a reloaded Harborline App can re-render the review).
/// This surface COMPLEMENTS the B-1a single-shot <c>POST /packs/export</c> (kept for tooling/tests): the
/// ceremony is the path the Composer UI drives, because the human review + affirmation are inseparable from
/// the snapshot (they cannot be a UI checkbox — S-1).
/// </summary>
public static class PackComposeRoutes
{
    /// <summary>The compose route base.</summary>
    public const string ComposeRoute = "/api/local-node/packs/compose";

    /// <summary>Maps the compose/affirm/export ceremony routes onto the shared inner app, closing over the
    /// outer-container dependencies (bug-2849: the routes map onto the shared inner <c>WebApplication</c>).</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ComposeCeremony ceremony,
        IPackExporter exporter,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ceremony);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // POST /packs/compose — snapshot-at-compose.
        app.MapPost(ComposeRoute, async (HttpContext http, ComposeRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Version))
            {
                return Results.BadRequest(new { error = "compose request must carry a key + version." });
            }
            if (!Enum.TryParse<PackScopeTier>(request.ScopeTier, ignoreCase: true, out var scopeTier))
            {
                return Results.BadRequest(new { error = $"unknown scopeTier '{request.ScopeTier}'." });
            }

            var tenant = NodeTenant.Resolve(activeTeam);
            // The act composes ONE named pack — that pack is the record target, so a grant scoped to a
            // different pack refuses here.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, Authority(http, tenant, time), PackOperation.Author, request.Key, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var authoringPrincipal = signer.IssuerId.ToBase64Url();
            var dcp = BuildDcp(request.Dcp, authoringPrincipal);

            var ceremonyRequest = new ComposeRequest(
                ComposeId: request.ComposeId,
                Key: request.Key,
                Version: request.Version,
                Name: request.Name,
                Description: request.Description,
                ScopeTier: scopeTier,
                TypeIds: request.TypeIds ?? Array.Empty<string>(),
                Dcp: dcp,
                Dependencies: (request.Dependencies ?? Array.Empty<ComposeDependencyDto>())
                    .Select(d => new PackDependencyRef(d.Key, d.Version, d.DeclaredDependencyKeys ?? Array.Empty<string>()))
                    .ToList(),
                CapabilityRequirements: request.CapabilityRequirements ?? Array.Empty<string>(),
                FormIds: request.FormIds ?? Array.Empty<string>());

            var outcome = await ceremony.ComposeAsync(ceremonyRequest, tenant, ct).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case ComposeStatus.Ok:
                    logger.LogInformation(
                        "Pack COMPOSED (tenant {Tenant}, pack {Key} v{Version}, {Leaves} leaves, snapshot {Hash}).",
                        tenant, outcome.Draft!.Key, outcome.Draft.Version, outcome.Draft.Leaves.Count,
                        Truncate(outcome.Draft.SnapshotHash));
                    return Results.Ok(ToComposeResponse(outcome.Draft));
                case ComposeStatus.ValidationFailed:
                    return Results.UnprocessableEntity(new
                    {
                        error = "compose failed validation",
                        codes = outcome.Errors.Select(e => new { e.Code, e.Target }).ToList(),
                    });
                default:
                    return Results.Problem("compose failed", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // GET /packs/compose/{id} — re-read a draft (for a reloaded Harborline App to re-render the review).
        app.MapGet($"{ComposeRoute}/{{id}}", async (HttpContext http, string id, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var draft = ceremony.GetDraft(id, tenant);
            // The draft names the pack this act reads; a draft that does not exist names none, and the
            // install-wide shape is what the caller must then hold to learn that it is absent.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, Authority(http, tenant, time), PackOperation.Author, draft?.Key, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            return draft is null
                ? Results.NotFound(new { error = "no draft composition with that id." })
                : Results.Ok(ToComposeResponse(draft));
        });

        // POST /packs/compose/{id}/affirm — bind the human PII-review affirmation to the snapshot hash (S-1).
        app.MapPost($"{ComposeRoute}/{{id}}/affirm", async (HttpContext http, string id, AffirmRequestDto body, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var refusal = await PackRouteAuthorization
                .RefusalAsync(
                    gate, Authority(http, tenant, time), PackOperation.Author,
                    ceremony.GetDraft(id, tenant)?.Key, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var outcome = ceremony.Affirm(id, tenant, body?.SnapshotHash ?? string.Empty);
            switch (outcome.Status)
            {
                case ComposeStatus.Ok:
                    logger.LogInformation(
                        "Pack PII-review AFFIRMED (tenant {Tenant}, compose {Id}, snapshot {Hash}).",
                        tenant, id, Truncate(outcome.Draft!.SnapshotHash));
                    return Results.Ok(new AffirmResponseDto(id, true, outcome.Draft.SnapshotHash));
                case ComposeStatus.NotFound:
                    return Results.NotFound(new { error = "no draft composition with that id." });
                case ComposeStatus.SnapshotMismatch:
                    // The client affirmed a hash that is not the current snapshot — S-1 refuses the binding.
                    return Results.Conflict(new
                    {
                        error = "the reviewed content no longer matches this pack — re-review before affirming.",
                        code = "pack.compose.snapshot_mismatch",
                        currentSnapshotHash = outcome.Draft?.SnapshotHash,
                    });
                default:
                    return Results.Problem("affirm failed", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // POST /packs/compose/{id}/export — re-hash + guarded own-roster sign through the DCP gate.
        app.MapPost($"{ComposeRoute}/{{id}}/export", async (HttpContext http, string id, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var refusal = await PackRouteAuthorization
                .RefusalAsync(
                    gate, Authority(http, tenant, time), PackOperation.Author,
                    ceremony.GetDraft(id, tenant)?.Key, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }
            var outcome = await ceremony
                .ExportAsync(id, tenant, exporter, signer, PackComposerRoutes.OwnRosterEpoch, ct)
                .ConfigureAwait(false);
            switch (outcome.Status)
            {
                case ComposeStatus.Ok:
                    logger.LogInformation(
                        "Pack EXPORTED via ceremony (tenant {Tenant}, compose {Id}, {Bytes} bytes).",
                        tenant, id, outcome.FileBytes!.Length);
                    return Results.File(outcome.FileBytes!, "application/octet-stream", outcome.FileName);
                case ComposeStatus.NotFound:
                    return Results.NotFound(new { error = "no draft composition with that id." });
                case ComposeStatus.AffirmationRequired:
                    return Results.UnprocessableEntity(new
                    {
                        error = "the human privacy review must be completed before export.",
                        code = "pack.compose.affirmation_required",
                    });
                case ComposeStatus.SnapshotMismatch:
                    return Results.Conflict(new
                    {
                        error = "the content changed after review — re-review before export.",
                        code = "pack.compose.snapshot_mismatch",
                    });
                case ComposeStatus.ValidationFailed:
                    return Results.UnprocessableEntity(new
                    {
                        error = "pack failed validation",
                        codes = outcome.Errors.Select(e => new { e.Code, e.Target }).ToList(),
                    });
                default:
                    return Results.Problem("export failed", statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static AuthorizationWriteContext Authority(HttpContext http, TenantId tenant, TimeProvider time) =>
        PackRouteAuthorization.Authority(http, tenant, time);

    /// <summary>
    /// Maps the minimal wire DCP (or its absence) to a <see cref="DomainComplianceProfile"/>. A missing DTO
    /// GRANDFATHERS to a <c>general</c> profile (ADR 0145 compatibility plan) whose RACI solo-collapses to
    /// the authoring principal (the #127 General-pack dogfood case). A supplied DTO overrides the class +
    /// selected fields; the export gate then validates whatever is produced — a non-counsel-cleared class
    /// HARD-BLOCKS at export (Q1 / D4). The rich counsel fields (regimes, jurisdictional variance,
    /// out-of-scope) stay at the general shape until the Wave-2 DCP editor fills them.
    /// </summary>
    private static DomainComplianceProfile BuildDcp(ComposeDcpDto? dto, string authoringPrincipal)
    {
        var general = DomainComplianceProfile.General(authoringPrincipal);
        if (dto is null)
        {
            return general;
        }

        var regulatoryClass = Enum.TryParse<RegulatoryClass>(
            (dto.RegulatoryClass ?? string.Empty).Replace("-", string.Empty), ignoreCase: true, out var rc)
            ? rc
            : general.RegulatoryClass;
        var dataSensitivity = Enum.TryParse<DataSensitivityClass>(dto.DataSensitivity, ignoreCase: true, out var ds)
            ? ds
            : general.DataSensitivity;

        return general with
        {
            RegulatoryClass = regulatoryClass,
            DataSensitivity = dataSensitivity,
            DcpVersion = string.IsNullOrWhiteSpace(dto.DcpVersion) ? general.DcpVersion : dto.DcpVersion,
        };
    }

    private static ComposeResponseDto ToComposeResponse(DraftComposition draft) => new(
        ComposeId: draft.ComposeId,
        SnapshotHash: draft.SnapshotHash,
        Key: draft.Key,
        Version: draft.Version,
        Name: draft.Name,
        Description: draft.Description,
        ScopeTier: draft.ScopeTier.ToString(),
        Leaves: draft.Leaves.Select(l => new ComposeLeafDto(
            l.Key,
            l.Kind.ToString(),
            l.Version,
            l.ContentAddress,
            // The EXACT canonical bytes the human reviews + we sign (S-1 — every leaf, not a subset).
            Encoding.UTF8.GetString(CanonicalJson.Serialize<JsonNode>(l.Content)))).ToList(),
        Dcp: draft.Dcp is { } d ? new ComposeDcpSummaryDto(d.RegulatoryClass.ToString(), d.DcpVersion) : null,
        Affirmed: draft.Affirmation is not null,
        // Advisory compose-time warnings (#141) — localizable code + params, no English (the client renders).
        Warnings: draft.Warnings.Select(w => new ComposeWarningDto(w.Code, w.Target, w.Params)).ToList());

    private static string Truncate(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";
}

// ── Wire DTOs (local to the route) ─────────────────────────────────────────────

/// <summary>The compose request body. v1 selects asset types and published forms by id.</summary>
public sealed record ComposeRequestDto(
    string? ComposeId,
    string Key,
    string Version,
    string? Name,
    string? Description,
    string ScopeTier,
    IReadOnlyList<string>? TypeIds,
    ComposeDcpDto? Dcp = null,
    IReadOnlyList<ComposeDependencyDto>? Dependencies = null,
    IReadOnlyList<string>? CapabilityRequirements = null,
    IReadOnlyList<string>? FormIds = null);

/// <summary>The minimal wire DCP the compose surface sends. The rich counsel fields (out-of-scope, regimes,
/// jurisdictional variance) are Wave-2; v1 carries the class + version over the <c>general</c> grandfather.</summary>
public sealed record ComposeDcpDto(
    string? RegulatoryClass,
    string? DataSensitivity,
    string? DcpVersion);

/// <summary>One declared dependency in a compose request.</summary>
public sealed record ComposeDependencyDto(
    string Key,
    string Version,
    IReadOnlyList<string>? DeclaredDependencyKeys);

/// <summary>The affirm request body — the snapshot hash the human reviewed (bound server-side, S-1).</summary>
public sealed record AffirmRequestDto(string SnapshotHash);

/// <summary>The compose / get-draft response.</summary>
public sealed record ComposeResponseDto(
    string ComposeId,
    string SnapshotHash,
    string Key,
    string Version,
    string Name,
    string Description,
    string ScopeTier,
    IReadOnlyList<ComposeLeafDto> Leaves,
    ComposeDcpSummaryDto? Dcp,
    bool Affirmed,
    IReadOnlyList<ComposeWarningDto> Warnings);

/// <summary>One frozen leaf in a compose response — its content-address + the CANONICAL text the human
/// reviews for private data (S-1 — the review inspects these exact bytes).</summary>
public sealed record ComposeLeafDto(
    string Key,
    string Kind,
    string Version,
    string ContentAddress,
    string CanonicalText);

/// <summary>A lean DCP summary on the compose response (class + version).</summary>
public sealed record ComposeDcpSummaryDto(string RegulatoryClass, string DcpVersion);

/// <summary>One advisory compose-time warning (#141) — a stable, localizable <see cref="Code"/>, the optional
/// offending <see cref="Target"/> (an asset-type id), and structured <see cref="Params"/> the client
/// interpolates. Carries NO English: the client renders the message from a catalog keyed by
/// <see cref="Code"/> (the validation-codes doctrine).</summary>
public sealed record ComposeWarningDto(string Code, string? Target, IReadOnlyDictionary<string, string> Params);

/// <summary>The affirm response — echoes the bound snapshot hash.</summary>
public sealed record AffirmResponseDto(string ComposeId, bool Affirmed, string SnapshotHash);
