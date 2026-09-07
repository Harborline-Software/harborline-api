using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local <b>instance-lifecycle</b> surface (ADR 0144 AD.1 — the setup-phase mechanics, slice B5). Two
/// routes the Harborline App's lifecycle-aware Build fold consumes:
/// <list type="bullet">
///   <item><b>Read</b> — <c>GET /api/local-node/governance/lifecycle</c> returns the tenant's current
///     <see cref="SetupPhase"/> + the <c>workshop:unlock</c> decision (an accessible grant/denial). The
///     Harborline App phase hook (<c>useLifecyclePhase.ts</c>) reads this to fold Build + render the guarded
///     re-entry (slice B4). Reading a tenant with no state yet establishes genesis (phase = setup).</item>
///   <item><b>Finish setup</b> — <c>POST /api/local-node/governance/lifecycle/finish-setup</c> is the
///     founder-declared setup → operating transition — the UNGUARDED safe direction (reversible, reduces
///     surface). Idempotent.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Only re-entry (unlock) is guarded — and that guard is NOT here.</b> This surface moves the phase in
/// the safe direction only; re-entering Build in operating phase is the Harborline App's guarded control (slice
/// B4), authorized by <see cref="INodeWorkshopUnlockAuthority"/> (surfaced on the GET as the <c>unlock</c>
/// decision so B4 can render the accessible denial without re-deriving it). There is deliberately no
/// "unlock" mutation route here — unlocking opens Build within operating phase, it does not move the phase.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4).</b> Like every non-allowlisted <c>/api/local-node/*</c> route this is gated by
/// the listener-level caller-auth middleware; the per-route check is defence-in-depth. The read is a pure
/// projection; the finish-setup POST is the one mutation (idempotent).
/// </para>
/// </remarks>
public static class LifecycleRoutes
{
    /// <summary>Canonical route base for the instance-lifecycle surface.</summary>
    public const string RouteBase = "/api/local-node/governance/lifecycle";

    /// <summary>The founder-declared setup → operating transition route.</summary>
    public const string FinishSetupRoute = RouteBase + "/finish-setup";

    /// <summary>Maps the lifecycle read + finish-setup routes onto <paramref name="app"/>, closing over the
    /// governance store, the unlock authority, the active-team accessor, the clock, and the caller-auth
    /// guard.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ITenantGovernanceStateStore store,
        INodeWorkshopUnlockAuthority unlockAuthority,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        NodeCallerSessionToken callerAuth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(unlockAuthority);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(callerAuth);

        MapRead(app, store, unlockAuthority, activeTeam, timeProvider, callerAuth);
        MapFinishSetup(app, store, unlockAuthority, activeTeam, timeProvider, callerAuth);
    }

    // ── GET /api/local-node/governance/lifecycle ─────────────────────────────────────────────────────
    private static void MapRead(
        IEndpointRouteBuilder app,
        ITenantGovernanceStateStore store,
        INodeWorkshopUnlockAuthority unlockAuthority,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        NodeCallerSessionToken callerAuth)
    {
        app.MapGet(RouteBase, async (HttpContext http, CancellationToken ct) =>
        {
            if (callerAuth.Validate(http.Request) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            var tenant = TryResolveTenant(activeTeam);
            if (tenant is null)
            {
                // Pre-genesis boot window (no active team yet): a node with no tenant is conceptually still
                // in setup. Return a calm default WITHOUT persisting; the Harborline App keeps polling.
                return Results.Ok(LifecycleResponse.PreGenesis());
            }

            // Reading a tenant with no state yet establishes genesis (phase = setup) — idempotent.
            var state = await store.EnsureGenesisAsync(tenant.Value.Value, ct).ConfigureAwait(false);
            var unlock = await unlockAuthority
                .AuthorizeAsync(Authority(http, tenant.Value, timeProvider), ct).ConfigureAwait(false);
            return Results.Ok(LifecycleResponse.From(state, unlock));
        });
    }

    // ── POST /api/local-node/governance/lifecycle/finish-setup ───────────────────────────────────────
    private static void MapFinishSetup(
        IEndpointRouteBuilder app,
        ITenantGovernanceStateStore store,
        INodeWorkshopUnlockAuthority unlockAuthority,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        NodeCallerSessionToken callerAuth)
    {
        app.MapPost(FinishSetupRoute, async (HttpContext http, CancellationToken ct) =>
        {
            if (callerAuth.Validate(http.Request) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            var tenant = TryResolveTenant(activeTeam);
            if (tenant is null)
            {
                // No active tenant to lock down — the founder cannot declare setup complete on a node with
                // no instance yet. Fail loud (not a silent success) so the Harborline App surfaces a retry.
                return Results.UnprocessableEntity(new { error = "no_active_tenant", detail = "No active tenant — the instance is not ready to finish setup yet." });
            }

            // setup → operating: the UNGUARDED safe direction. Idempotent (already-operating is a no-op).
            var state = await store.FinishSetupAsync(tenant.Value.Value, ct).ConfigureAwait(false);
            var unlock = await unlockAuthority
                .AuthorizeAsync(Authority(http, tenant.Value, timeProvider), ct).ConfigureAwait(false);
            return Results.Ok(LifecycleResponse.From(state, unlock));
        });
    }

    /// <summary>The server-derived principal / tenant / act instant for this request — built HERE, at the
    /// point of use, and handed to the unlock authority; nothing downstream reads an ambient scope
    /// (ticket 205, ledger L592). Same shape the gated record routes use.</summary>
    // The unlock authority resolves this through the gate, so the actor is the grant SUBJECT (the shared
    // NodeGatePrincipal), not the attribution party — same reading as every other route guard.
    private static AuthorizationWriteContext Authority(HttpContext http, TenantId tenant, TimeProvider timeProvider) =>
        RequestAuthorization.Authority(http, tenant, timeProvider);

    /// <summary>Resolves the active-team-derived tenant, or <see langword="null"/> during the pre-bootstrap
    /// window (no active team) — so the routes degrade calmly instead of 500-ing.</summary>
    private static TenantId? TryResolveTenant(IActiveTeamAccessor activeTeam)
    {
        var active = activeTeam.Active;
        if (active is null)
        {
            return null;
        }
        return ActiveTeamTenantContext.ProjectTenantId(active.TeamId);
    }
}

// ── Wire shapes (camelCase JSON — the FED useLifecyclePhase.ts contract) ─────────────────────────────

/// <summary>The lifecycle read/finish-setup response: the current phase + the workshop-unlock decision.</summary>
/// <param name="Phase"><c>"setup" | "operating"</c>.</param>
/// <param name="Unlock">The <c>workshop:unlock</c> decision (accessible grant/denial) for the guarded
/// re-entry the Harborline App renders in operating phase (slice B4).</param>
public sealed record LifecycleResponse(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("unlock")] UnlockDecisionWire Unlock)
{
    /// <summary>Projects a <see cref="TenantGovernanceState"/> + a <see cref="WorkshopUnlockDecision"/> onto
    /// the wire shape.</summary>
    public static LifecycleResponse From(TenantGovernanceState state, WorkshopUnlockDecision decision) => new(
        Phase: SetupPhaseWire.ToWire(state.SetupPhase),
        Unlock: UnlockDecisionWire.From(decision));

    /// <summary>The pre-genesis calm default: setup phase, unlock not yet resolvable (no tenant).</summary>
    public static LifecycleResponse PreGenesis() => new(
        Phase: SetupPhaseWire.Setup,
        Unlock: new UnlockDecisionWire(
            Granted: false,
            ReasonCode: "no_active_tenant",
            ReasonKey: null,
            RemediationKey: null));
}

/// <summary>The <c>workshop:unlock</c> decision on the wire — a grant, or a denial with accessible
/// reason/remediation i18n keys (never a bare bool).</summary>
/// <param name="Granted">True iff the principal holds <c>workshop:unlock</c>.</param>
/// <param name="ReasonCode">Stable machine code for a denial cause; null when granted.</param>
/// <param name="ReasonKey">i18n key for the human-readable denial cause; null when granted.</param>
/// <param name="RemediationKey">i18n key for the suggested next action on denial; null when granted.</param>
public sealed record UnlockDecisionWire(
    [property: JsonPropertyName("granted")] bool Granted,
    [property: JsonPropertyName("reasonCode")] string? ReasonCode,
    [property: JsonPropertyName("reasonKey")] string? ReasonKey,
    [property: JsonPropertyName("remediationKey")] string? RemediationKey)
{
    /// <summary>Projects a <see cref="WorkshopUnlockDecision"/> onto the wire shape.</summary>
    public static UnlockDecisionWire From(WorkshopUnlockDecision decision) => decision switch
    {
        WorkshopUnlockDecision.Granted => new UnlockDecisionWire(true, null, null, null),
        WorkshopUnlockDecision.Denied d => new UnlockDecisionWire(false, d.ReasonCode, d.ReasonKey, d.RemediationKey),
        _ => new UnlockDecisionWire(false, "unknown", null, null),
    };
}
