using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Pack Composer B-1a — the node-local EXPORT + VERIFY routes (design note 2026-07-02 §2.4 / §7).
/// <c>POST /api/local-node/packs/export</c> composes + validates + signs a pack into a
/// sneakernet-able file; <c>POST /api/local-node/packs/verify</c> runs the verify-before-effect
/// pipeline over an uploaded pack file and returns a typed verdict. This is the ONLY node surface
/// B-1a adds — there is NO install route (that is B-1b): verify NEVER mutates any store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-scoping (INV-S1).</b> Exactly as the sibling routes: the Harborline App sends no tenant id;
/// the route resolves it from the active team (<see cref="NodeTenant.Resolve"/>) and audits under it.
/// </para>
/// <para>
/// <b>Trust roots.</b> v1 verifies against the node's OWN roster (the node principal signer) at the
/// own-roster pack epoch (<see cref="OwnRosterEpoch"/>). The Harborline-channel root is a v1 trust
/// root too, but a node CONSUMES channel packs at install (B-1b) — this thin B-1a surface signs +
/// self-verifies own-roster packs; wiring the channel key + revocation list is the B-1b install
/// path's obligation (security fold S-11).
/// </para>
/// <para>
/// <b>Audited (minimal floor).</b> Export (an outbound act, S-5) and verify are audited via a
/// structured log line carrying the tenant + pack key + outcome. Durable audit-ENVELOPE wiring is a
/// B-1b concern (install is the state-mutating step that needs the transactional audit row); B-1a
/// exports/reads, so the structured-log floor is the appropriate minimal audit here.
/// </para>
/// </remarks>
public static class PackComposerRoutes
{
    /// <summary>Route for composing + signing a pack file.</summary>
    public const string ExportRoute = "/api/local-node/packs/export";

    /// <summary>Route for verifying an uploaded pack file.</summary>
    public const string VerifyRoute = "/api/local-node/packs/verify";

    /// <summary>
    /// The node's own-roster pack epoch for v1 (ADR 0126 D4). A fixed baseline: export signs at this
    /// epoch and verify recognizes the own-roster key at this epoch. Key rotation / epoch lineage
    /// (a retired epoch reporting <c>EpochUnverifiable</c>) is a B-1b / rotation concern; the machinery
    /// is already present in the verifier + trust store — only the epoch-advance wiring is deferred.
    /// </summary>
    public const long OwnRosterEpoch = 1;

    /// <summary>Maps the export + verify routes, closing over the outer-container dependencies
    /// (resolved here, not via <c>[FromServices]</c>, because the routes map onto the shared inner
    /// <c>WebApplication</c> — bug-2849).</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IPackExporter exporter,
        IPackVerifier verifier,
        IPackTrustStore trustStore,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // POST /api/local-node/packs/export — compose + validate + sign. AUTHOR-side (council A-1):
        // gated on `packages:author` at the ROUTE (the UI rail is a reflection, not the enforcement).
        app.MapPost(ExportRoute, async (HttpContext http, ExportPackRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key))
            {
                return Results.BadRequest(new { error = "Request must carry a pack key." });
            }
            var tenant = NodeTenant.Resolve(activeTeam);
            // The single-shot export names its pack in the body — that pack is the record target.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Author, request.Key, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            if (!Enum.TryParse<PackScopeTier>(request.ScopeTier, ignoreCase: true, out var scopeTier))
            {
                return Results.BadRequest(new { error = $"Unknown scopeTier '{request.ScopeTier}'." });
            }

            var contents = new List<PackContentSource>();
            foreach (var c in request.Contents ?? Array.Empty<ExportContentDto>())
            {
                if (!Enum.TryParse<PackContentKind>(c.Kind, ignoreCase: true, out var kind))
                {
                    return Results.BadRequest(new { error = $"Unknown content kind '{c.Kind}' for '{c.Key}'." });
                }
                // Bind the arbitrary JSON body into a JsonNode for canonicalization.
                var node = c.Content is { } el ? JsonNode.Parse(el.GetRawText()) : null;
                if (node is null)
                {
                    return Results.BadRequest(new { error = $"Content '{c.Key}' has no body." });
                }
                contents.Add(new PackContentSource(c.Key, kind, c.Version, node));
            }

            var dependencies = (request.Dependencies ?? Array.Empty<ExportDependencyDto>())
                .Select(d => new PackDependencyRef(d.Key, d.Version, d.DeclaredDependencyKeys ?? Array.Empty<string>()))
                .ToList();

            // Map the declared DCP (ADR 0145). A client that omits one is GRANDFATHERED to a `general`
            // profile (compatibility plan); a client declaring a higher class must supply a real DCP, which
            // the export gate then blocks unless the class is counsel-cleared (D4 / S-13).
            var authoringPrincipal = signer.IssuerId.ToBase64Url();
            if (!TryBuildDcp(request.Dcp, authoringPrincipal, out var dcp, out var dcpError))
            {
                return Results.BadRequest(new { error = dcpError });
            }

            var exportRequest = new PackExportRequest(
                Key: request.Key,
                Version: request.Version,
                Name: request.Name ?? request.Key,
                Description: request.Description ?? string.Empty,
                ScopeTier: scopeTier,
                Contents: contents,
                Dependencies: dependencies,
                CapabilityRequirements: request.CapabilityRequirements ?? Array.Empty<string>(),
                Epoch: OwnRosterEpoch,
                ProviderSlot: string.IsNullOrWhiteSpace(request.ProviderSlot) ? null : request.ProviderSlot.Trim(),
                Dcp: dcp);

            var outcome = await exporter.ExportAsync(exportRequest, signer, ct).ConfigureAwait(false);
            if (!outcome.Succeeded || outcome.FileBytes is null)
            {
                logger.LogInformation(
                    "Pack export REFUSED (tenant {Tenant}, pack {Key}) — {ErrorCount} validation error(s).",
                    tenant, request.Key, outcome.Validation.Errors.Count);
                return Results.UnprocessableEntity(new
                {
                    error = "pack failed validation",
                    codes = outcome.Validation.Errors.Select(e => new { e.Code, e.Target }).ToList(),
                });
            }

            logger.LogInformation(
                "Pack EXPORTED (tenant {Tenant}, pack {Key} v{Version}, {Bytes} bytes).",
                tenant, request.Key, request.Version, outcome.FileBytes.Length);
            return Results.File(outcome.FileBytes, "application/octet-stream", $"{request.Key}-{request.Version}.pack");
        });

        // POST /api/local-node/packs/verify — verify-before-effect over an uploaded pack file. AUTHOR-side
        // (verify-your-own-pack; council A-1): gated on `packages:author`.
        app.MapPost(VerifyRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var httpRequest = http.Request;
            var tenant = NodeTenant.Resolve(activeTeam);
            // Verify NEVER mutates and the uploaded artifact is not yet a pack record — the act addresses
            // the install, so it carries no record target and rides the install-wide declaration.
            var refusal = await PackRouteAuthorization
                .RefusalAsync(gate, PackRouteAuthorization.Authority(http, tenant, time), PackOperation.Author, null, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            using var buffer = new MemoryStream();
            await httpRequest.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            var bytes = buffer.ToArray();

            var result = verifier.Verify(bytes, trustStore);

            logger.LogInformation(
                "Pack VERIFY (tenant {Tenant}) → {Verdict} [{Details}].",
                tenant, result.Verdict, string.Join(",", result.Details));

            return Results.Ok(new VerifyPackResponseDto(
                Verdict: result.Verdict.ToString(),
                Details: result.Details,
                SignerKeyId: result.SignerKeyId?.ToBase64Url(),
                Epoch: result.Epoch,
                VouchingScope: result.VouchingScope?.ToString(),
                // Only the manifest KEY is surfaced on the thin verdict — the full manifest/content is
                // exposed to the install engine (B-1b), never leaked by this read surface. Null unless
                // fully Verified (S-7 verify-before-effect).
                ManifestKey: result.Manifest?.Key));
        });
    }

    /// <summary>
    /// Maps the wire DCP (or its absence) to a <see cref="DomainComplianceProfile"/>. A missing DTO is the
    /// GRANDFATHER case (ADR 0145 compatibility plan): a <c>general</c> profile whose RACI solo-collapses to
    /// <paramref name="authoringPrincipal"/>. A supplied DTO overrides the class + selected fields; the rich
    /// counsel fields (regimes / jurisdictional variance) default to the general shape until the B-2a DCP
    /// edit surface fills them. The export gate then validates whatever is produced (presence is guaranteed
    /// here; the class-clearance + FK checks still bite).
    /// </summary>
    private static bool TryBuildDcp(
        ExportDcpDto? dto,
        string authoringPrincipal,
        out DomainComplianceProfile dcp,
        out string? error)
    {
        var general = DomainComplianceProfile.General(authoringPrincipal);
        error = null;
        if (dto is null)
        {
            dcp = general;
            return true;
        }

        // A DECLARED-but-unparseable compliance token is rejected fail-closed — never
        // silently coerced to the `general` default (ADR 0145 honesty). A typo'd
        // regulated class ("healthcare-typo") must NOT downgrade to General and slip
        // past the export gate's class-clearance check. An OMITTED (null/empty) token
        // still legitimately grandfathers to general — only a supplied-but-bad token
        // is a 400. Same treatment for dataSensitivity (identical silent-coerce seam).
        if (!TryParseRegulatoryClass(dto.RegulatoryClass, general.RegulatoryClass, out var regulatoryClass))
        {
            dcp = general;
            error = $"Unknown regulatoryClass '{dto.RegulatoryClass}'.";
            return false;
        }
        if (!TryParseDataSensitivity(dto.DataSensitivity, general.DataSensitivity, out var dataSensitivity))
        {
            dcp = general;
            error = $"Unknown dataSensitivity '{dto.DataSensitivity}'.";
            return false;
        }

        var outOfScope = (dto.OutOfScope ?? Array.Empty<ExportOutOfScopeDto>())
            .Select(o => new DcpOutOfScopeDeclaration(o.Scope, o.CounselDisclaimerRef))
            .ToList();

        dcp = general with
        {
            RegulatoryClass = regulatoryClass,
            DataSensitivity = dataSensitivity,
            DcpVersion = string.IsNullOrWhiteSpace(dto.DcpVersion) ? general.DcpVersion : dto.DcpVersion,
            Audience = general.Audience with
            {
                Audience = string.IsNullOrWhiteSpace(dto.Audience) ? general.Audience.Audience : dto.Audience,
                MinorsAsSubjects = dto.MinorsAsSubjects ?? general.Audience.MinorsAsSubjects,
                MinorsAsPrincipals = dto.MinorsAsPrincipals ?? general.Audience.MinorsAsPrincipals,
            },
            OutOfScope = outOfScope,
        };
        return true;
    }

    /// <summary>Omitted ⇒ <paramref name="fallback"/> (grandfather). Supplied-and-valid ⇒ parsed.
    /// Supplied-but-unparseable ⇒ <c>false</c> (the caller rejects with a 400 — never a silent downgrade).</summary>
    private static bool TryParseRegulatoryClass(string? raw, RegulatoryClass fallback, out RegulatoryClass value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }
        return Enum.TryParse(raw.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out value);
    }

    /// <summary>Same contract as <see cref="TryParseRegulatoryClass"/> for the data-sensitivity class.</summary>
    private static bool TryParseDataSensitivity(string? raw, DataSensitivityClass fallback, out DataSensitivityClass value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }
        return Enum.TryParse(raw, ignoreCase: true, out value);
    }
}

/// <summary>Shared fail-closed, accessible authorization-denial shape for the <c>/packs/*</c> routes
/// (council A-1). Stable localizable <c>code</c> + the required <c>permission</c> so the Harborline App renders an
/// accessible denial; the English <c>error</c> is a developer aid only.</summary>
internal static class PackRouteAuthz
{
    /// <summary>The stable, localizable denial code the Harborline App localizes off.</summary>
    public const string DeniedCode = "pack.authz.denied";

    /// <summary>A 403 fail-closed denial naming the missing permission.</summary>
    public static IResult Denied(string permission) => Results.Json(
        new { error = "You do not have permission for this action.", code = DeniedCode, permission },
        statusCode: StatusCodes.Status403Forbidden);
}

// ── Wire DTOs (local to the route) ─────────────────────────────────────────────

/// <summary>The export request body.</summary>
public sealed record ExportPackRequestDto(
    string Key,
    string Version,
    string? Name,
    string? Description,
    string ScopeTier,
    IReadOnlyList<ExportContentDto>? Contents,
    IReadOnlyList<ExportDependencyDto>? Dependencies,
    IReadOnlyList<string>? CapabilityRequirements,
    string? ProviderSlot = null,
    ExportDcpDto? Dcp = null);

/// <summary>The declared Domain Compliance Profile (ADR 0145). Omitted ⇒ grandfathered to <c>general</c>.
/// A lean v1 surface — the B-2a DCP edit surface extends it for the rich counsel fields.</summary>
public sealed record ExportDcpDto(
    string? RegulatoryClass,
    string? DataSensitivity,
    string? DcpVersion,
    string? Audience,
    bool? MinorsAsSubjects,
    bool? MinorsAsPrincipals,
    IReadOnlyList<ExportOutOfScopeDto>? OutOfScope);

/// <summary>One out-of-scope declaration in a wire DCP — scope + a counsel disclaimer FK (never free
/// text, D-12).</summary>
public sealed record ExportOutOfScopeDto(string Scope, string CounselDisclaimerRef);

/// <summary>One content item in an export request — the body is arbitrary JSON.</summary>
public sealed record ExportContentDto(
    string Key,
    string Kind,
    string Version,
    System.Text.Json.JsonElement? Content);

/// <summary>One declared dependency in an export request.</summary>
public sealed record ExportDependencyDto(
    string Key,
    string Version,
    IReadOnlyList<string>? DeclaredDependencyKeys);

/// <summary>The verify verdict response.</summary>
public sealed record VerifyPackResponseDto(
    string Verdict,
    IReadOnlyList<string> Details,
    string? SignerKeyId,
    long? Epoch,
    string? VouchingScope,
    string? ManifestKey);
