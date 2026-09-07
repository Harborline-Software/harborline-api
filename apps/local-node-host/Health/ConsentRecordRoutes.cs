using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The node-local subject-consent record surface (ticket 213, ledger L646) — the product callers of the
/// four lifecycle transitions the gate already knew how to write, plus a read of the EFFECTIVE record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes.</b> <c>POST /api/local-node/consent-records</c> requests a record;
/// <c>POST …/{recordId}/activate|expire|revoke</c> moves it; <c>GET …/{recordId}</c> reads it.
/// </para>
/// <para>
/// <b>One decision at the point of use.</b> Every route resolves ONE
/// <c>Harborline.Api.Foundation.Authorization.AuthorizationGate</c> decision through the shared ticket-205
/// guard, against the record the act addresses. The three transition routes and the read name the consent
/// record; the REQUEST route addresses the install, because its record does not exist to be scoped to yet
/// — which the gate admits only because <c>consent:write</c> is declared install-wide on the definition
/// side (<c>PermissionVocabulary.InstallWideOperations</c>), never because this call site said so.
/// </para>
/// <para>
/// <b>One clock read per act.</b> The route builds its authority once
/// (<see cref="RequestAuthorization.Authority"/>, over the kernel <see cref="TimeProvider"/>), decides the
/// act on that instant, and dates the transition and its audit row with the SAME instant — so a consent
/// activation can never be recorded as having happened at an instant other than the one it was authorized
/// at, and a caller cannot backdate one by passing a timestamp (none of these routes accepts one).
/// </para>
/// <para>
/// <b>The route is not the authority.</b> The gate predicate still answers with no route
/// (<c>IConsentGate.DecideAsync</c>); these routes only WRITE the records it reads, so slice 1's
/// route-less point-of-use test is unaffected by their existence.
/// </para>
/// </remarks>
public static class ConsentRecordRoutes
{
    /// <summary>Canonical route base for the node-local subject-consent record surface.</summary>
    public const string RouteBase = "/api/local-node/consent-records";

    /// <summary>Maps the consent-record routes onto <paramref name="app"/>, closing over the gate.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        TenantConsentGate gate,
        ITenantConsentStore store,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);

        // ONE tenant resolution point for the whole family (ADR 0160 R3-D posture, and the shape ticket
        // 205 slice 4 collapsed the other route families onto): every handler below reads the tenant
        // through this one delegate rather than naming the process-global accessor itself.
        Func<TenantId> tenantOf = () => NodeTenant.Resolve(activeTeam);

        // ── GET /api/local-node/consent-records/{recordId} — the EFFECTIVE record ──────────────
        app.MapGet($"{RouteBase}/{{recordId}}", async (
            string recordId, HttpContext http, CancellationToken ct) =>
        {
            var tenant = tenantOf();
            if (Authority(http, tenant) is not { } authority) return RequestAuthorization.Denied(Permission.ConsentRead);
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, Permission.ConsentRead, RouteRecord.Of(recordId), ct) is { } denied)
                return denied;

            var record = await FindAsync(store, tenant, recordId, ct).ConfigureAwait(false);
            return record is null ? Results.NotFound() : Results.Ok(ConsentRecordWire.From(record, authority.At));
        });

        // ── POST /api/local-node/consent-records — request ─────────────────────────────────────
        app.MapPost(RouteBase, async (
            ConsentRequestBody? body, HttpContext http, CancellationToken ct) =>
        {
            var tenant = tenantOf();
            if (Authority(http, tenant) is not { } authority) return RequestAuthorization.Denied(Permission.ConsentWrite);
            // The requested record does not exist yet, so the act addresses the install rather than a record.
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, Permission.ConsentWrite, RouteRecord.TheInstall, ct) is { } denied)
                return denied;

            if (string.IsNullOrWhiteSpace(body?.Subject) || string.IsNullOrWhiteSpace(body.Purpose)
                || string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { title = "subject_purpose_and_scope_are_required" });

            ScopeExpression scope;
            try { scope = ScopeExpression.Parse(body.Scope); }
            catch (ArgumentException) { return Results.BadRequest(new { title = "invalid_scope" }); }

            var record = TenantConsentRecord.Request(
                Guid.NewGuid().ToString("N"), tenant, new SubjectId(body.Subject), body.Purpose, scope,
                authority.At, body.EffectiveUntil, body.SignatureConsentRecordId);
            var written = await gate.RequestAsync(record, authority.Principal, ct).ConfigureAwait(false);
            return Results.Ok(ConsentRecordWire.From(written, authority.At));
        });

        MapTransition(app, gate, store, tenantOf, "activate",
            (g, record, actor, at, ct) => g.ActivateAsync(record, actor, at, ct));
        MapTransition(app, gate, store, tenantOf, "expire",
            (g, record, actor, at, ct) => g.ExpireAsync(record, actor, at, ct));
        MapTransition(app, gate, store, tenantOf, "revoke",
            (g, record, actor, at, ct) => g.RevokeAsync(record, actor, at, ct));
    }

    /// <summary>
    /// The three transition routes are one shape: decide the act against the record it addresses, load that
    /// record, move it. They differ only in which move they ask for, so they share one body rather than
    /// three copies that could drift apart on the guard.
    /// </summary>
    private static void MapTransition(
        IEndpointRouteBuilder app,
        TenantConsentGate gate,
        ITenantConsentStore store,
        Func<TenantId> tenantOf,
        string transition,
        Func<TenantConsentGate, TenantConsentRecord, ActorId, DateTimeOffset, CancellationToken,
            Task<TenantConsentRecord>> move)
    {
        app.MapPost($"{RouteBase}/{{recordId}}/{transition}", async (
            string recordId, HttpContext http, CancellationToken ct) =>
        {
            var tenant = tenantOf();
            if (Authority(http, tenant) is not { } authority) return RequestAuthorization.Denied(Permission.ConsentWrite);
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, Permission.ConsentWrite, RouteRecord.Of(recordId), ct) is { } denied)
                return denied;

            var record = await FindAsync(store, tenant, recordId, ct).ConfigureAwait(false);
            if (record is null) return Results.NotFound();

            try
            {
                var moved = await move(gate, record, authority.Principal, authority.At, ct).ConfigureAwait(false);
                return Results.Ok(ConsentRecordWire.From(moved, authority.At));
            }
            catch (ConsentLifecycleTransitionException ex)
            {
                // The record refused the move before anything was persisted (the pure record decides), so
                // this is the caller asking for an impossible transition, not a failure of the write.
                return Results.Conflict(new
                {
                    title = "invalid_consent_transition",
                    from = ex.From.ToString(),
                    to = ex.To.ToString(),
                });
            }
        });
    }

    /// <summary>
    /// The request's own authority — the acting principal, the tenant, and the ONE instant this act is
    /// decided on and recorded with. Null when the container holds no kernel clock, which is not "allowed":
    /// there is no instant to date the act with and deliberately no wall-clock fallback.
    /// </summary>
    private static AuthorizationWriteContext? Authority(HttpContext http, TenantId tenant)
    {
        var time = http.RequestServices.GetService<TimeProvider>();
        return time is null ? null : RequestAuthorization.Authority(http, tenant, time);
    }

    private static async ValueTask<TenantConsentRecord?> FindAsync(
        ITenantConsentStore store, TenantId tenant, string recordId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return null;
        var records = await store.ReadAsync(tenant, ct).ConfigureAwait(false);
        return records.FirstOrDefault(r => string.Equals(r.Id, recordId, StringComparison.Ordinal));
    }
}

/// <summary>The body of a consent REQUEST. It carries no instant: the route's own clock read dates it.</summary>
public sealed record ConsentRequestBody(
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("purpose")] string? Purpose,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("effective_until")] DateTimeOffset? EffectiveUntil,
    [property: JsonPropertyName("signature_consent_record_id")] string? SignatureConsentRecordId);

/// <summary>
/// One consent record on the wire. <c>effective_state</c> is the state the record is ACTUALLY in at the
/// instant of the read (<c>TenantConsentRecord.StateAt</c>) — the same reading the gate decides by — while
/// <c>state</c> is what is stored. They differ exactly when a stored <c>Active</c> row's window has closed
/// and no sweep has caught up with it yet.
/// </summary>
public sealed record ConsentRecordWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("effective_state")] string EffectiveState,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("effective_from")] DateTimeOffset? EffectiveFrom,
    [property: JsonPropertyName("effective_until")] DateTimeOffset? EffectiveUntil,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("signature_consent_record_id")] string? SignatureConsentRecordId)
{
    /// <summary>Projects <paramref name="record"/> as it reads at <paramref name="at"/>.</summary>
    public static ConsentRecordWire From(TenantConsentRecord record, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ConsentRecordWire(
            record.Id, record.Subject.Value, record.Purpose, record.Scope.Value,
            record.State.ToString(), record.StateAt(at).ToString(), record.RequestedAt,
            record.EffectiveFrom, record.EffectiveUntil, record.RevokedAt,
            record.SignatureConsentRecordId);
    }
}
