using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local dynamic-FORMS routes (render + submit)
/// onto the shared Kestrel listener (ADR 0055 wiring amendment, 2026-06-25). This
/// is the host that finally CONSUMES <see cref="IFormEngine"/> — the engine was
/// built + hardened + durable but unwired (ONR survey, earlier repository ticket #1460).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="FormsRoutes.Map"/> (the single source of truth shared with the
/// route tests so the wire contract has no test/prod drift). The engine, the
/// capability issuer/verifier, and the active-team accessor are resolved from the
/// OUTER host container and passed to <see cref="FormsRoutes.Map"/> as
/// closed-over dependencies. Resolving via <c>[FromServices]</c> inside the route
/// handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service
/// provider does NOT have the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// <b>Acting identity (MTW-2 #3378).</b> The form subject and the definition owner
/// are resolved PER REQUEST from <c>NodeCallerParty.Resolve(http)</c> — the live
/// selected-session principal on <c>HttpContext.Features</c>, falling back to the
/// operator party on the desktop plane. They used to be constants computed once
/// here at <c>StartAsync</c>, which recorded every member's work against the same
/// identity: a member whose form subject and owner is a constant has not been
/// correctly identified. The capability the route mints is scoped to the resolved
/// subject + the active-team tenant.
/// </para>
/// <para>
/// <b>But attribution is not authorization, and that is why the fence stays (card #3367).</b>
/// Roles still come from the resolved <see cref="ICurrentUser"/> when present (the
/// active-team display role), else the route's
/// <see cref="FormsRoutes.NodeOperatorRole"/> default — captured ONCE at startup and
/// replayed on every request, which is DESKTOP-plane authority and nothing else.
/// #3378 was scoped to ATTRIBUTION; per-member permissions are MTW-3 (CIC 2026-07-29).
/// So a member is now correctly NAMED and still judged by whatever role the operator
/// held at boot: with #3378 merged, a signed-in member's submit still returned 201 or
/// 403 purely on that. Because these routes mint from captured role values instead of
/// resolving an <see cref="Harborline.Api.Foundation.Authorization.IAuthorizationContext"/>,
/// the web-plane fence that once stood at that seam (card #3356, deleted with the seam by
/// ticket 205 slice 5) structurally could not see this family — a seam-gate cannot reach
/// code that has already left the seam.
/// </para>
/// <para>
/// So the two families registered HERE — <see cref="FormsRoutes"/> and
/// <see cref="FormDefinitionRoutes"/> — are mapped into
/// <see cref="WebPlaneUnavailableRouteFence"/>'s group and are unavailable while a
/// hosted-web principal is bound (ADR 0160 D5).
/// </para>
/// <para>
/// <b>NOT "the whole forms surface" — say what is covered, not what sounds complete.</b>
/// <see cref="FormDraftRoutes"/> declares the SAME <c>/api/local-node/forms</c> route
/// base but is registered elsewhere (<see cref="HostedFormDraftsApiEndpoint"/>), so
/// mapping the families HERE never reached it. It stayed open on the web plane and was
/// the same deputy by a third mechanism — its handlers resolve <c>IPartyContext</c> from
/// the OUTER container, so a web-plane member's draft was keyed to the operator's party
/// and the list route returned the operator's drafts to that member. Found by the deep
/// review of this PR, which is exactly the kind of thing a "whole family" claim hides.
/// <b>Now fixed:</b> that family opens its own fenced group at its own registration site,
/// and the disclosure was observed failing a test before the fix. The lesson survives the
/// fix — a route base does not imply a fence, because fence membership is not queryable,
/// so mapping one family proves nothing about a sibling that registers elsewhere.
/// Fail-closed until MTW-3 makes authorization real; the fence comes off then, not
/// before (CIC ruling 2026-07-31).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while the
/// shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedFormsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IFormEngine _engine;
    private readonly IFormCapabilityIssuer _issuer;
    private readonly IFormCapabilityVerifier _verifier;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizedFormDefinitionLifecycle _definitionStore;
    private readonly ISchemaRegistry _schemaRegistry;
    private readonly ICurrentUser? _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly IFormSubmissionGate? _submissionGate;
    private readonly IRestrictingDefinitionKindValidator _restrictingKinds;
    private readonly ILogger<HostedFormsApiEndpoint> _logger;

    /// <summary>Constructs the hosted dynamic-forms API endpoint.</summary>
    public HostedFormsApiEndpoint(
        SharedHostedWebApp sharedApp,
        IFormEngine engine,
        IFormCapabilityIssuer issuer,
        IFormCapabilityVerifier verifier,
        IActiveTeamAccessor activeTeam,
        AuthorizedFormDefinitionLifecycle definitionStore,
        ISchemaRegistry schemaRegistry,
        ILogger<HostedFormsApiEndpoint> logger,
        ICurrentUser? currentUser = null,
        TimeProvider? timeProvider = null,
        IRestrictingDefinitionKindValidator? restrictingKinds = null,
        IFormSubmissionGate? submissionGate = null)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _engine = engine;
        _issuer = issuer;
        _verifier = verifier;
        _activeTeam = activeTeam;
        _definitionStore = definitionStore;
        _schemaRegistry = schemaRegistry;
        _currentUser = currentUser;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _restrictingKinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;
        _submissionGate = submissionGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The acting member is resolved per request inside the route handlers (#3378), NOT captured
        // here: StartAsync runs once at boot, so anything computed at this point is the same value
        // for every member who ever acts. Roles remain host-wide — attribution is this card, and
        // per-member permissions are MTW-3.
        var roles = _currentUser?.Roles ?? Array.Empty<string>();

        _sharedApp.MapApiRoutes(app =>
        {
            // ⚠ SECURITY (card #3367) — `roles` above is captured ONCE, here at startup, and replayed on
            // every request. ONLY roles: `subject` and `owner` used to be captured here too, and are now
            // resolved PER REQUEST inside the route files (#3437 / issue #3378) — do not re-read this
            // banner as saying otherwise, and do not "restore" a captured subject or owner to match it.
            //
            // A startup-captured ROLE is still the desktop operator's authority, which is the whole
            // defect: a member is correctly NAMED and still judged by whatever role the operator held at
            // boot. So every route mapped below goes into the desktop-plane-only group: while a
            // hosted-web request principal is bound the endpoint refuses instead of minting a capability
            // against that role (ADR 0160 D5 — consume the request principal or be unavailable).
            //
            // Map new routes for this family onto `desktopPlaneOnly`, never onto `app`. Mapping onto
            // `app` compiles, serves, and silently reopens the confused deputy; the fence is a property
            // of the group, not of the route. FormsStartupCapturedIdentityFenceTests drives BOTH families
            // below on the web plane, so dropping either from the group turns a test red.
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();

            // Runtime surface: render + submit a published form (the tenant USER fills it in).
            // BOTH halves, and the distinction matters. IDENTITY comes from the per-request resolution
            // earlier repository ticket #3437 landed (which removed the captured `subject` / `owner` parameters this
            // family used to pass). EXPOSURE comes from card #3367's fence — mapped onto
            // `desktopPlaneOnly`, never onto `app`.
            //
            // Keeping only the identity half leaves a member correctly NAMED and still judged by
            // whatever role the operator held at boot — the deputy #3367 exists to close, which #3437
            // deliberately left open because per-member permissions are MTW-3. Keeping only the fence
            // half re-introduces the constant identity #3437 removed. In a diff those two mistakes and
            // the correct merge look nearly identical, so this comment is the guard.
            FormsRoutes.Map(desktopPlaneOnly, _engine, _issuer, _verifier, _activeTeam, roles, _timeProvider, _submissionGate);
            // Authoring surface: save + load + list a FormDefinition (the tenant ADMIN authors it
            // in the Harborline App form builder). Real persistence over IFormDefinitionStore — production
            // slice 1 (2026-06-27).
            FormDefinitionRoutes.Map(
                desktopPlaneOnly, _definitionStore, _schemaRegistry, _activeTeam, _timeProvider, _restrictingKinds);
        });

        _logger.LogInformation(
            "ADR 0055 node-local dynamic-forms API registered — the engine is now LIVE " +
            "(GET {RouteBase}/{{formId}}, POST {RouteBase}/{{formId}}/submit; " +
            "GET/PUT {DefBase}[/{{formId}}] form-definition authoring). First-party forms only; " +
            "packet-carried definitions are gated on the ADR-0135-A1 CP-reachability validator (follow-up).",
            FormsRoutes.RouteBase,
            FormsRoutes.RouteBase,
            FormDefinitionRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
