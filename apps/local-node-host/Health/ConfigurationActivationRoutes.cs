using System.Text.Json;

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
/// T-644: the transport over the api's atomic activation target. One read of the effective generation, one
/// preparation of an isolated candidate against an explicit expected baseline, and one compare-and-swap that
/// makes a prepared candidate effective or refuses with a named code. Every handler observes the admitted
/// instant exactly once (ADR 0081) and carries it through the authority it resolves at point of use.
/// </summary>
internal static class ConfigurationActivationRoutes
{
    /// <summary>Route: the effective configuration generation governing the tenant.</summary>
    public const string EffectiveRoute = "/api/local-node/configuration/effective";

    /// <summary>Route: prepare and validate an isolated candidate against the expected baseline.</summary>
    public const string PrepareRoute = "/api/local-node/configuration/prepare";

    /// <summary>Route: the atomic compare-and-swap of a prepared candidate.</summary>
    public const string ActivateRoute = "/api/local-node/configuration/activate";

    /// <summary>Route: run a declared verification suite against a prepared candidate (T-463).</summary>
    public const string VerifyRoute = "/api/local-node/configuration/verify";

    public static void Map(IEndpointRouteBuilder app, ConfigurationActivationTarget target,
        IActiveTeamAccessor activeTeam, AuthorizationGate gate, TimeProvider time, ILogger logger,
        VerificationRunner? verification = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        var selectedSession = app.MapSelectedSessionProductGroup();
        // ADR 0160 R3 ledger: this family resolves the tenant at one point per file, per request.
        TenantId Tenant() => NodeTenant.Resolve(activeTeam);

        selectedSession.MapGet(EffectiveRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var tenant = Tenant();
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization.RefusalAsync(gate, authority, PackOperation.Operate, null, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            try
            {
                var effective = target.ReadEffective(tenant);
                return Results.Ok(new EffectiveGenerationDto(effective.Digest, effective.Algorithm, effective.References));
            }
            catch (ArgumentException exception)
            {
                return Results.NotFound(new { code = exception.Message, target = "effective" });
            }
        });

        selectedSession.MapPost(PrepareRoute, async (HttpContext http, PrepareConfigurationRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ExpectedBaselineDigest)
                || request.ActivePackageKeys is null || request.ActivePackageKeys.Count == 0)
                return Results.BadRequest(new { error = "expectedBaselineDigest and activePackageKeys are required." });
            var tenant = Tenant();
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization.RefusalAsync(gate, authority, PackOperation.Operate, null, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            var ownership = (request.Ownership ?? [])
                .Where(owner => !string.IsNullOrWhiteSpace(owner.DefinitionKey) && !string.IsNullOrWhiteSpace(owner.PackageKey))
                .ToDictionary(owner => owner.DefinitionKey, owner => owner.PackageKey, StringComparer.Ordinal);
            HostConfigurationPreparation prepared;
            try
            {
                prepared = target.Prepare(tenant, request.ExpectedBaselineDigest, request.ActivePackageKeys, ownership, authority.At);
            }
            catch (ArgumentException exception)
            {
                return Results.NotFound(new { code = exception.Message, target = "effective" });
            }
            var dto = ToDto(prepared, candidateDigest: null);
            // CA1873: the params array is built before the level is consulted; the guard is the remediation.
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Configuration PREPARE (tenant {Tenant}) → {Status} candidate={Candidate}", tenant, dto.Status, dto.CandidateDigest);
            return dto.Status == "refused" ? Results.UnprocessableEntity(dto) : Results.Ok(dto);
        });

        selectedSession.MapPost(ActivateRoute, async (HttpContext http, ActivateConfigurationRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ExpectedBaselineDigest)
                || string.IsNullOrWhiteSpace(request.CandidateDigest) || request.EvidenceIntent is null
                || string.IsNullOrWhiteSpace(request.EvidenceIntent.Id) || string.IsNullOrWhiteSpace(request.EvidenceIntent.Reason))
                return Results.BadRequest(new { error = "expectedBaselineDigest, candidateDigest and evidenceIntent {id, reason} are required." });
            var tenant = Tenant();
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization.RefusalAsync(gate, authority, PackOperation.Operate, null, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            var intent = new ConfigurationEvidenceIntent(request.EvidenceIntent.Id, request.EvidenceIntent.Reason);
            var principal = authority.Principal.Value;

            // A re-request after a lost acknowledgement is answered by evidence intent identity from the
            // committed outbox: the same inputs are already effective, different inputs are a reuse refusal.
            var acknowledged = target.Acknowledged(tenant, intent, request.CandidateDigest, request.ExpectedBaselineDigest, principal);
            if (acknowledged is not null)
            {
                return acknowledged.InputsMatch
                    ? Results.Ok(new ActivationDto("effective", tenant.Value, acknowledged.NewDigest, acknowledged.PriorDigest,
                        acknowledged.NewDigest, [], Acknowledged: true))
                    : Results.UnprocessableEntity(new ActivationDto("refused", tenant.Value, request.CandidateDigest,
                        request.ExpectedBaselineDigest, acknowledged.NewDigest,
                        [new("configuration-evidence-intent-reused", "evidenceIntent", "The evidence intent identity was committed with different inputs.")]));
            }

            HostConfigurationPreparation reprepared;
            try
            {
                reprepared = target.Reprepare(tenant, request.ExpectedBaselineDigest, request.CandidateDigest);
            }
            catch (ArgumentException exception)
            {
                return Results.NotFound(new { code = exception.Message, target = "effective" });
            }
            if (reprepared.Preparation?.Prepared is null)
                return Results.UnprocessableEntity(ToDto(reprepared, request.CandidateDigest));

            ConfigurationActivationOutcome outcome;
            try
            {
                outcome = await target.For(authority)
                    .CompareAndSwapAsync(new(reprepared.Preparation.Prepared, principal, intent), ct)
                    .ConfigureAwait(false);
            }
            catch (ConfigurationCommitIndeterminateException exception)
            {
                logger.LogError(exception, "Configuration ACTIVATE (tenant {Tenant}) commit acknowledgement lost for intent {Intent}.", tenant, exception.EvidenceIntentId);
                return Results.Json(new { status = "indeterminate", evidenceIntentId = exception.EvidenceIntentId, code = exception.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            var bindings = ConfigurationActivationDetail.Bind(outcome);
            var result = new ActivationDto(outcome.Decision.Refusal is null ? "effective" : "refused", tenant.Value,
                outcome.Decision.Request.Prepared.Candidate.Digest, outcome.Decision.Request.Prepared.Baseline.Digest,
                outcome.EffectiveGeneration.Digest,
                outcome.Decision.Refusal is null ? [] : [ToDto(outcome.Decision.Refusal)], Detail: bindings);
            // CA1873, as on PREPARE: guard so the params array is not built when Information is off.
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Configuration ACTIVATE (tenant {Tenant}) → {Status} effective={Effective}", tenant, result.Status, result.EffectiveDigest);
            return result.Status == "effective" ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });

        // T-463: verifying a candidate is part of AUTHORING a change — it is what produces the receipt a
        // proposed change records as its check — so it resolves packages:author, the same side of the ck-8
        // split /proposals/{id}/checks draws, and not the packages:operate that activation resolves. It is
        // also the only route in this family that changes nothing: the run writes no record, emits no
        // evidence and cannot move the effective pointer, whatever the candidate's own rules say.
        if (verification is null) return;
        selectedSession.MapPost(VerifyRoute, async (HttpContext http, VerifyConfigurationRequestDto request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ExpectedBaselineDigest)
                || string.IsNullOrWhiteSpace(request.CandidateDigest) || string.IsNullOrWhiteSpace(request.ReceiptId)
                || string.IsNullOrWhiteSpace(request.Suite))
                return Results.BadRequest(new { error = "expectedBaselineDigest, candidateDigest, receiptId and suite are required." });
            var tenant = Tenant();
            var authority = PackRouteAuthorization.Authority(http, tenant, time);
            var refusal = await PackRouteAuthorization.RefusalAsync(gate, authority, PackOperation.Author, null, ct).ConfigureAwait(false);
            if (refusal is not null) return refusal;

            // The suite arrives as a wire document and is re-admitted through the platform's own Declare,
            // so a case this host would have refused at authoring cannot be smuggled in here and run.
            var suite = VerificationSuite.Parse(request.Suite, out var admission);
            if (suite is null)
                return Results.UnprocessableEntity(new VerificationRunDto("refused", tenant.Value, request.ReceiptId,
                    request.CandidateDigest, request.ExpectedBaselineDigest, [.. admission.Select(ToDto)]));

            var run = await verification.RunAsync(tenant, request.ReceiptId, request.CandidateDigest,
                request.ExpectedBaselineDigest, suite, ct).ConfigureAwait(false);
            if (run.Receipt is not { } receipt)
                return Results.UnprocessableEntity(new VerificationRunDto("refused", tenant.Value, request.ReceiptId,
                    request.CandidateDigest, request.ExpectedBaselineDigest, [.. run.Refusals.Select(ToDto)]));

            var dto = new VerificationRunDto(receipt.Status.ToString(), receipt.TenantKey, receipt.ReceiptId,
                receipt.CandidateDigest, receipt.BaselineDigest, [], receipt.Digest,
                VerificationDetail.Bind(receipt, suite));
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Configuration VERIFY (tenant {Tenant}, receipt {Receipt}) → {Status} {Digest}",
                    tenant, receipt.ReceiptId, dto.Status, receipt.Digest);
            return Results.Ok(dto);
        });
    }

    private static ConfigurationRefusalDto ToDto(VerificationRefusal refusal) => new(refusal.Code, refusal.Target, refusal.Message);

    private static ActivationDto ToDto(HostConfigurationPreparation prepared, string? candidateDigest)
    {
        if (prepared.Preparation is { } preparation)
        {
            return new ActivationDto(preparation.Prepared is null ? "refused" : "preparing",
                prepared.Baseline.References.GetProperty("tenantKey").GetString()!, preparation.Candidate.Digest,
                preparation.ExpectedBaselineDigest, preparation.Baseline.Digest, preparation.Refusals.Select(ToDto).ToArray(),
                Projection: preparation.Prepared is null ? null : new(preparation.Prepared.Projection.Key,
                    preparation.Prepared.Projection.Revision, preparation.Prepared.Projection.Digest),
                Detail: ConfigurationActivationDetail.Bind(preparation));
        }
        return new ActivationDto("refused", prepared.Baseline.References.GetProperty("tenantKey").GetString()!,
            candidateDigest ?? string.Empty, string.Empty, prepared.Baseline.Digest, [ToDto(prepared.HostRefusal!)]);
    }

    private static ConfigurationRefusalDto ToDto(ConfigurationActivationRefusal refusal) => new(refusal.Code, refusal.Target, refusal.Message);
}

/// <summary>The effective generation: identity plus the canonical public references it was derived from.</summary>
public sealed record EffectiveGenerationDto(string Digest, string Algorithm, JsonElement References);

/// <summary>An ownership selection: which package supplies one definition.</summary>
public sealed record ConfigurationOwnershipDto(string DefinitionKey, string PackageKey);

/// <summary>Prepare an isolated candidate over installed Active packs against the expected baseline.</summary>
public sealed record PrepareConfigurationRequestDto(string ExpectedBaselineDigest, IReadOnlyList<string> ActivePackageKeys,
    IReadOnlyList<ConfigurationOwnershipDto>? Ownership);

/// <summary>The evidence intent the switch commits with.</summary>
public sealed record EvidenceIntentDto(string Id, string Reason);

/// <summary>Switch a prepared candidate, naming the baseline it was prepared against.</summary>
public sealed record ActivateConfigurationRequestDto(string ExpectedBaselineDigest, string CandidateDigest, EvidenceIntentDto EvidenceIntent);

/// <summary>A stable refusal code, its target, and a message.</summary>
public sealed record ConfigurationRefusalDto(string Code, string Target, string Message);

/// <summary>An immutable projection reference.</summary>
public sealed record ProjectionReferenceDto(string Key, string Revision, string Digest);

/// <summary>Released-vocabulary status (preparing, refused, effective) with the bound digests and refusals.</summary>
public sealed record ActivationDto(string Status, string TenantKey, string CandidateDigest, string ExpectedBaselineDigest,
    string EffectiveDigest, IReadOnlyList<ConfigurationRefusalDto> Refusals, ProjectionReferenceDto? Projection = null,
    IReadOnlyDictionary<string, string>? Detail = null, bool Acknowledged = false);

/// <summary>Run one declared verification suite against a prepared candidate (T-463).</summary>
/// <param name="ExpectedBaselineDigest">The baseline the candidate must still prepare over.</param>
/// <param name="CandidateDigest">The prepared candidate to verify.</param>
/// <param name="ReceiptId">The host's stable identity for this run; a proposed change binds its check to it.</param>
/// <param name="Suite">The canonical suite document, re-admitted through the platform before anything runs.</param>
public sealed record VerifyConfigurationRequestDto(string ExpectedBaselineDigest, string CandidateDigest,
    string ReceiptId, string Suite);

/// <summary>
/// One verification run: the released outcome vocabulary, the digests the receipt binds, and the
/// read-only detail bindings both renderer lanes show. A refused run carries its refusals and no
/// receipt digest, because a run that did not complete has no evidence to offer.
/// </summary>
public sealed record VerificationRunDto(string Status, string TenantKey, string ReceiptId,
    string CandidateDigest, string BaselineDigest, IReadOnlyList<ConfigurationRefusalDto> Refusals,
    string? ReceiptDigest = null, IReadOnlyDictionary<string, string>? Detail = null);
