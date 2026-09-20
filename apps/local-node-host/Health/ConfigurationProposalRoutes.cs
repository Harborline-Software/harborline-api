using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T-461: the transport over propose, save and release. A domain expert starts a Proposed change from the
/// effective generation, autosaves edits away from operational users, freezes a Saved version with
/// authorship and rationale, and releases the exact checked candidate as a signed Released package.
/// </summary>
/// <remarks>
/// <para>
/// Every write carries the released detail bindings the platform produced, so no surface has to build a
/// step label or a digest line of its own. Every handler observes the admitted instant exactly once
/// (ADR 0081) and carries it through the authority it resolves at point of use.
/// </para>
/// <para>
/// None of these routes touches the effective pointer. Making a Released package effective is
/// <see cref="ConfigurationActivationRoutes"/>'s prepare-and-activate path, which is a separate act with
/// its own authority and its own atomic transaction.
/// </para>
/// </remarks>
internal static class ConfigurationProposalRoutes
{
    /// <summary>Route: start a Proposed change from the effective generation.</summary>
    public const string ProposalsRoute = "/api/local-node/configuration/proposals";

    /// <summary>Route: read one Proposed change and its released detail bindings.</summary>
    public const string ProposalRoute = "/api/local-node/configuration/proposals/{proposalId}";

    /// <summary>Route: autosave one edited definition into the Proposed change.</summary>
    public const string AutosaveRoute = "/api/local-node/configuration/proposals/{proposalId}/edits";

    /// <summary>Route: freeze the working edits as the next immutable Saved version.</summary>
    public const string SaveVersionRoute = "/api/local-node/configuration/proposals/{proposalId}/versions";

    /// <summary>Route: record a verification receipt against the exact working state.</summary>
    public const string CheckRoute = "/api/local-node/configuration/proposals/{proposalId}/checks";

    /// <summary>Route: release one Saved version as a signed Released package.</summary>
    public const string ReleaseRoute = "/api/local-node/configuration/proposals/{proposalId}/release";

    /// <summary>Route: the Released packages offered for activation, by their own artifact digest.</summary>
    public const string ReleasesRoute = "/api/local-node/configuration/releases";

    /// <summary>Route: install and activate one offered Released package, by its own artifact digest.</summary>
    public const string InstallReleaseRoute = "/api/local-node/configuration/releases/{digest}/install";

    public static void Map(IEndpointRouteBuilder app, ConfigurationProposalStore store,
        IActiveTeamAccessor activeTeam, AuthorizationGate gate, TimeProvider time, ILogger logger,
        ReleasedPackInstaller? releases = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        var selectedSession = app.MapSelectedSessionProductGroup();
        // ADR 0160 R3 ledger: this family resolves the tenant at one point per file, per request.
        TenantId Tenant() => NodeTenant.Resolve(activeTeam);

        // Authoring a proposed change is the AUTHOR capability; releasing one is OPERATE. That is the
        // ck-8 split the pack routes already draw, applied to the configuration loop: a tenant policy can
        // narrow an author out of releasing without inventing a new ceiling.
        async ValueTask<(AuthorizationWriteContext Authority, IResult? Refusal)> AuthorizeAsync(
            HttpContext http, TenantId tenant, Harborline.Api.Foundation.IdentityAtlas.Permissions.AuthorizationOperation operation,
            CancellationToken ct)
        {
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            return (authority, await PackRouteAuthorization.RefusalAsync(gate, authority, operation, null, ct).ConfigureAwait(false));
        }

        selectedSession.MapPost(ProposalsRoute, async (HttpContext http, StartProposedChangeDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ProposalId))
                return Results.BadRequest(new { error = "proposalId is required." });
            var tenant = Tenant();
            var (authority, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Author, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            try
            {
                var proposed = store.Start(tenant, request.ProposalId, authority.Principal.Value, authority.At);
                return Results.Ok(Dto(store, tenant, proposed));
            }
            catch (ArgumentException exception)
            {
                return Results.UnprocessableEntity(new { code = exception.Message, target = "proposalId" });
            }
        });

        selectedSession.MapGet(ProposalRoute, async (HttpContext http, string proposalId, CancellationToken ct) =>
        {
            var tenant = Tenant();
            var (_, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Author, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            var proposed = store.Read(tenant, proposalId);
            return proposed is null
                ? Results.NotFound(new { code = "configuration-proposal-missing", target = "proposalId" })
                : Results.Ok(Dto(store, tenant, proposed));
        });

        selectedSession.MapPut(AutosaveRoute, async (HttpContext http, string proposalId,
            AutosaveEditDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DefinitionKey)
                || string.IsNullOrWhiteSpace(request.PackageKey) || string.IsNullOrWhiteSpace(request.BodyJson)
                || string.IsNullOrWhiteSpace(request.ContentKind))
                return Results.BadRequest(new { error = "definitionKey, packageKey, bodyJson and contentKind are required." });
            var tenant = Tenant();
            var (authority, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Author, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            try
            {
                var proposed = store.Autosave(tenant, proposalId,
                    new(request.DefinitionKey, request.PackageKey, request.BodyJson, request.ContentKind), authority.At);
                return Results.Ok(Dto(store, tenant, proposed));
            }
            catch (ArgumentException exception)
            {
                return Unprocessable(exception, "edit");
            }
        });

        selectedSession.MapPost(SaveVersionRoute, async (HttpContext http, string proposalId,
            SaveVersionDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Rationale))
                return Results.BadRequest(new { error = "rationale is required." });
            var tenant = Tenant();
            var (authority, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Author, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            try
            {
                // The author is the server-derived acting principal, never a client-asserted name.
                var version = store.Save(tenant, proposalId, authority.Principal.Value, request.Rationale, authority.At);
                var proposed = store.Read(tenant, proposalId)!;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Configuration SAVE VERSION (tenant {Tenant}, proposal {Proposal}) → {Ordinal} {Digest}",
                        tenant, proposalId, version.Ordinal, version.Digest);
                return Results.Ok(Dto(store, tenant, proposed, version));
            }
            catch (ArgumentException exception)
            {
                return Unprocessable(exception, "savedVersion");
            }
        });

        selectedSession.MapPost(CheckRoute, async (HttpContext http, string proposalId,
            RecordCheckDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ReceiptId))
                return Results.BadRequest(new { error = "receiptId is required." });
            var tenant = Tenant();
            var (_, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Author, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            try
            {
                return Results.Ok(Dto(store, tenant, store.RecordCheck(tenant, proposalId, request.ReceiptId)));
            }
            catch (ArgumentException exception)
            {
                return Unprocessable(exception, "check");
            }
        });

        selectedSession.MapPost(ReleaseRoute, async (HttpContext http, string proposalId,
            ReleaseDto request, CancellationToken ct) =>
        {
            if (request is null || request.Ordinal < 1 || string.IsNullOrWhiteSpace(request.PackageKey)
                || string.IsNullOrWhiteSpace(request.Revision))
                return Results.BadRequest(new { error = "ordinal, packageKey and revision are required." });
            var tenant = Tenant();
            // Releasing is the OPERATE capability: authoring a change never confers the authority to
            // release it (DES-0044 governance-ck-9, point of use).
            var (authority, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Operate, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            ConfigurationReleaseResult result;
            HostReleasedPackage? stored;
            try
            {
                (result, stored) = await store.ReleaseAsync(tenant, proposalId, request.Ordinal,
                    request.PackageKey, request.Revision, authority, ct).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                return Unprocessable(exception, "savedVersion");
            }
            var proposed = store.Read(tenant, proposalId)!;
            var version = store.ReadSavedVersion(tenant, proposalId, request.Ordinal)!;
            var dto = Dto(store, tenant, proposed, version, result, stored);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Configuration RELEASE (tenant {Tenant}, proposal {Proposal}) → {Status} {Digest}",
                    tenant, proposalId, dto.Status, dto.ReleasedPackage?.Digest);
            return stored is null ? Results.UnprocessableEntity(dto) : Results.Ok(dto);
        });

        selectedSession.MapGet(ReleasesRoute, async (HttpContext http, string? proposalId, CancellationToken ct) =>
        {
            var tenant = Tenant();
            var (_, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Operate, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            // Every digest here is re-derived from the stored bytes, so this offer names the artifact.
            return Results.Ok(store.Offered(tenant, proposalId).Select(Offer).ToArray());
        });

        // T-667: the missing half of ADR 0097 decision 6. Release produced a provider-neutral document
        // and the installer consumes a signed pack file; this converts one into the other and installs
        // it, so what the author released is what POST /configuration/prepare can then resolve over.
        // Installing is OPERATE, as releasing is: it is the same act's other end, not a new ceiling.
        if (releases is not null)
            selectedSession.MapPost(InstallReleaseRoute, async (HttpContext http, string digest,
                InstallReleaseRequestDto? request, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(digest))
                    return Results.BadRequest(new { error = "digest is required." });
                var tenant = Tenant();
                var (authority, refusal) = await AuthorizeAsync(http, tenant, PackOperation.Operate, ct).ConfigureAwait(false);
                if (refusal is not null) return refusal;
                // The owning-pack choice per contested definition is the caller's, as it is on the pack
                // activate route: an edited definition always collides with the pack that owns it today.
                var ownership = (request?.Ownership ?? [])
                    .Where(owner => !string.IsNullOrWhiteSpace(owner.DefinitionKey) && !string.IsNullOrWhiteSpace(owner.PackageKey))
                    .ToDictionary(owner => owner.DefinitionKey, owner => owner.PackageKey, StringComparer.Ordinal);
                var outcome = await releases.InstallAsync(tenant, digest, authority, ownership, ct).ConfigureAwait(false);
                var dto = new InstalledReleaseDto(outcome.Installed ? "installed" : "refused", tenant.Value,
                    outcome.ReleasedDigest, outcome.PackKey, outcome.Version,
                    [.. outcome.Refusals.Select(item => new ConfigurationRefusalDto(item.Code, item.Target, item.Message))]);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Configuration INSTALL RELEASE (tenant {Tenant}, digest {Digest}) → {Status} {Pack}@{Version}",
                        tenant, digest, dto.Status, dto.PackKey, dto.Version);
                return outcome.Installed ? Results.Ok(dto) : Results.UnprocessableEntity(dto);
            });
    }

    private static IResult Unprocessable(ArgumentException exception, string target) =>
        exception.Message == "configuration-proposal-missing"
            ? Results.NotFound(new { code = exception.Message, target = "proposalId" })
            : Results.UnprocessableEntity(new { code = exception.Message, target });

    private static ReleasedPackageDto Offer(HostReleasedPackage offer) => new(offer.Released.Digest,
        offer.Released.PackageKey, offer.Released.Revision, offer.Released.ProposalId,
        offer.Released.SavedVersionDigest, offer.Released.BaselineDigest, offer.ReleasedBy, offer.ReleasedAt,
        offer.SignatureJson);

    private static ProposedChangeDto Dto(ConfigurationProposalStore store, TenantId tenant,
        HostProposedChange proposed, SavedVersion? version = null, ConfigurationReleaseResult? result = null,
        HostReleasedPackage? stored = null)
    {
        var effective = store.ReadEffective(tenant);
        var detail = result is null
            ? version is null
                ? ConfigurationProposalDetail.Proposed(proposed.State, effective, proposed.Check)
                : ConfigurationProposalDetail.Saved(proposed.State, effective, version, proposed.Check)
            : ConfigurationProposalDetail.Bind(proposed.State, effective, version!, proposed.Check, result);
        var status = result is null ? version is null ? "proposed" : "saved" : result.Released is null ? "saved" : "released";
        return new(status, tenant.Value, proposed.State.ProposalId, proposed.State.BaselineDigest,
            effective.Digest, ConfigurationProposal.WorkingDigest(proposed.State),
            proposed.State.Edits.Select(edit => new ProposedEditDto(edit.DefinitionKey, edit.PackageKey, edit.ContentKind)).ToArray(),
            proposed.SavedVersionCount,
            version is null ? null : new SavedVersionDto(version.Ordinal, version.Digest, version.Author,
                version.Rationale, version.SavedAt),
            proposed.Check is null ? null : new CheckDto(proposed.Check.ReceiptId, proposed.Check.CheckedDigest,
                ConfigurationProposal.IsCurrent(proposed.Check, proposed.State)),
            stored is null ? null : Offer(stored),
            result?.Refusal is null ? [] : [new ConfigurationRefusalDto(result.Refusal.Code, result.Refusal.Target, result.Refusal.Message)],
            detail);
    }
}

/// <summary>Start a Proposed change from the effective generation.</summary>
public sealed record StartProposedChangeDto(string ProposalId);

/// <summary>
/// Autosave one edited definition. The body is stored verbatim and never repaired, and the author
/// states the transport content kind the definition is (T-667): the node never derives a kind from a
/// definition key, so a kind it does not define is refused by name rather than guessed at install.
/// </summary>
public sealed record AutosaveEditDto(string DefinitionKey, string PackageKey, string BodyJson, string ContentKind);

/// <summary>Freeze a Saved version. The author is server-derived; only the rationale is supplied.</summary>
public sealed record SaveVersionDto(string Rationale);

/// <summary>Record a verification receipt against the exact working state.</summary>
public sealed record RecordCheckDto(string ReceiptId);

/// <summary>Release one Saved version as a signed Released package.</summary>
public sealed record ReleaseDto(int Ordinal, string PackageKey, string Revision);

/// <summary>One edited definition, by public identity and stated content kind; bodies are not echoed.</summary>
public sealed record ProposedEditDto(string DefinitionKey, string PackageKey, string ContentKind);

/// <summary>An immutable checkpoint with its authorship and rationale.</summary>
public sealed record SavedVersionDto(int Ordinal, string Digest, string Author, string Rationale, DateTimeOffset SavedAt);

/// <summary>A recorded check and whether it still describes the state now being edited.</summary>
public sealed record CheckDto(string ReceiptId, string CheckedDigest, bool IsCurrent);

/// <summary>Install one offered Released package, with the owning-pack choice for contested definitions.</summary>
/// <param name="Ownership">Which package owns each definition the released package also claims.</param>
public sealed record InstallReleaseRequestDto(IReadOnlyList<ConfigurationOwnershipDto>? Ownership);

/// <summary>An installed Released package: the artifact digest and the pack identity it installed under.</summary>
/// <param name="Status">"installed" or "refused".</param>
/// <param name="TenantKey">The tenant the release belongs to.</param>
/// <param name="Digest">The Released package's own artifact digest.</param>
/// <param name="PackKey">The pack key it installed under; empty when nothing installed.</param>
/// <param name="Version">The pack version it installed under; empty when nothing installed.</param>
/// <param name="Refusals">Why nothing was installed; empty on success.</param>
public sealed record InstalledReleaseDto(string Status, string TenantKey, string Digest, string PackKey,
    string Version, IReadOnlyList<ConfigurationRefusalDto> Refusals);

/// <summary>A Released package offered for activation. The digest is the artifact's own bytes.</summary>
public sealed record ReleasedPackageDto(string Digest, string PackageKey, string Revision, string ProposalId,
    string SavedVersionDigest, string BaselineDigest, string ReleasedBy, DateTimeOffset ReleasedAt, string Signature);

/// <summary>A Proposed change with the released Proposed change / Saved version / Released package bindings.</summary>
public sealed record ProposedChangeDto(string Status, string TenantKey, string ProposalId, string BaselineDigest,
    string EffectiveDigest, string WorkingDigest, IReadOnlyList<ProposedEditDto> Edits, int SavedVersionCount,
    SavedVersionDto? SavedVersion, CheckDto? Check, ReleasedPackageDto? ReleasedPackage,
    IReadOnlyList<ConfigurationRefusalDto> Refusals, IReadOnlyDictionary<string, string> Detail);
