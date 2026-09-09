using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// DEV-ONLY KG-search seeder (Harborline App KG keyword-search demo, ONR survey
/// <c>onr-carrier-kg-search-calendar-demo-survey-2026-06-24</c>). On a development node it (1) projects the
/// dev-seeded <see cref="CalendarEvent"/>s into the KG <c>search_nodes</c> index as
/// <c>NodeType="calendar-event"</c> rows, and (2) seeds the acting OS-user principal an explicit
/// record-scoped access grants naming EXACTLY those seeded event ids — so the
/// fail-closed KG clip (<see cref="AuthorizedRecordScope"/> / <see cref="NodeSearchReadService"/>) returns
/// the events for that principal, and ONLY that principal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the demo realization of the Slice-0 KG-keyword substrate — no new ADR.</b> The substrate
/// (<see cref="NodeSearchReadService"/> / <see cref="NodeSearchIndexer"/> / the grant-backed clip) is
/// already built + DI-registered with ZERO production drivers. This dev seeder is the FIRST driver: it
/// writes the index rows (the indexer drive) AND seeds the grant (the load-bearing security step). Both are
/// behind an airtight Development-only gate.
/// </para>
/// <para>
/// <b>The dev gate is AIRTIGHT — fail-safe OFF.</b> The seed runs ONLY when <c>IsDevelopment()</c> is true;
/// explicit seed flags cannot widen it. A Production host never runs this — neither the index write
/// NOR the grant seed, so a real tenant's index is never polluted and no principal is ever auto-granted in
/// production.
/// </para>
/// <para>
/// <b>The principal-id footgun — PINNED (survey open Q#1).</b> The KG search route resolves the acting
/// principal SERVER-SIDE from the roster's current node key. The grant store keys grants on
/// <c>AccessGrant.Subject.Value</c> (ordinal string equality, both the in-memory and node-EF stores). So
/// this seeder uses the SAME helper to derive the grant's <see cref="AccessGrant.Subject"/> — the grant
/// and the route therefore key on the IDENTICAL string. If these two diverged, the clip would drop every
/// row. They share one source of truth (the helper) by construction. There is NO base64url transform on this
/// KG-clip path; the grant store keys on the raw <c>PrincipalId.Value</c> string directly.
/// </para>
/// <para>
/// <b>Which grant store — the LIVE one (survey open Q#2).</b> The vector composition (Slice 1d) REPLACEs
/// <see cref="IGrantStore"/> with the durable <c>NodeEfGrantStore</c>; the Slice-0 default is the
/// <c>InMemoryGrantStore</c>. This seeder takes whichever <see cref="IGrantStore"/> DI resolves — so it
/// always seeds into the LIVE store, never a dead one.
/// </para>
/// <para>
/// <b>Idempotent + ordered.</b> Registered AFTER <see cref="CalendarDevSeeder"/> (events exist first). The
/// index pass skips a record already indexed; the grant pass skips when an active grant for the principal
/// already names the events. A node restart re-runs cleanly — no duplicate index rows, no accreting grants.
/// </para>
/// <para>
/// <b>Through the clip, never around it.</b> This seeder makes the principal GENUINELY authorized (a real
/// <c>/records/&lt;id&gt;</c> grants) so the production read path returns the rows with no
/// special-casing. It does NOT add any "demo bypass" route that reads <c>search_nodes</c> directly — that
/// would be the no-mock-crypto anti-pattern (a dev gate silently becoming load-bearing for confidentiality).
/// </para>
/// </remarks>
public sealed class KgCalendarDevIndexer : IHostedService
{
    /// <summary>The KG node-type discriminator for an indexed calendar event (free-text, additive).</summary>
    public const string CalendarEventNodeType = "calendar-event";

    /// <summary>The deterministic dev grant-issuer (an opaque demo-data author id — not a real principal).</summary>
    private static readonly ActorId SeedGranter = new("dev-seed-granter");

    private readonly ICalendarEventStore _eventStore;
    private readonly IGrantStore _grantStore;
    private readonly NodeSearchIndexer _indexer;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeTeamRoster _roster;
    private readonly NodePrincipalSigner _nodeSigner;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<KgCalendarDevIndexer> _logger;
    private readonly AuthorizationGate? _gate;
    private readonly TimeProvider _time;

    /// <summary>The narrow, grant-backed principal used only by the Development projection.</summary>
    public const string DevIndexerPrincipal = AccessGrantAuthorizationSeed.DevIndexerPrincipal;

    public KgCalendarDevIndexer(
        ICalendarEventStore eventStore,
        IGrantStore grantStore,
        NodeSearchIndexer indexer,
        IActiveTeamAccessor activeTeam,
        NodeTeamRoster roster,
        NodePrincipalSigner nodeSigner,
        IHostEnvironment environment,
        ILogger<KgCalendarDevIndexer> logger,
        AuthorizationGate? gate = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(grantStore);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(nodeSigner);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _eventStore = eventStore;
        _grantStore = grantStore;
        _indexer = indexer;
        _activeTeam = activeTeam;
        _roster = roster;
        _nodeSigner = nodeSigner;
        _environment = environment;
        _logger = logger;
        _gate = gate;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Ticket 199: this independent projector is Development-only; seed flags never widen it in Production.
        if (!_environment.IsDevelopment())
        {
            _logger.LogDebug(
                "KgCalendarDevIndexer: environment '{Environment}' is not Development — skipping the KG "
                + "calendar-event index + grant seed (production-safe).",
                _environment.EnvironmentName);
            return;
        }

        TenantId tenantId;
        try
        {
            tenantId = NodeTenant.Resolve(_activeTeam);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "KgCalendarDevIndexer: no active team resolved — skipping the KG seed. (Register this hosted "
                + "service AFTER MultiTeamBootstrapHostedService + CalendarDevSeeder.)");
            return;
        }

        var events = await _eventStore.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
        {
            _logger.LogDebug(
                "KgCalendarDevIndexer: tenant {TenantId} has no seeded calendar events — nothing to index "
                + "(register AFTER CalendarDevSeeder so events exist).",
                tenantId);
            return;
        }

        var recordIds = await IndexEventsAsync(tenantId, events, cancellationToken).ConfigureAwait(false);
        await SeedPrincipalGrantAsync(tenantId, recordIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects each seeded calendar event into a <c>search_nodes</c> row (NodeType="calendar-event"). The
    /// <see cref="SearchNodeRow.Body"/> projection concatenates the humanized resource display + participant
    /// values + description + occupancy so the demo terms (<c>Dr. Smith</c> / <c>stand-up</c> / <c>consult</c>)
    /// all hit. Returns the indexed record ids (the grant scope).
    /// </summary>
    private async Task<IReadOnlyList<string>> IndexEventsAsync(
        TenantId tenantId, IReadOnlyList<CalendarEvent> events, CancellationToken ct)
    {
        var recordIds = new List<string>(events.Count);
        foreach (var ev in events)
        {
            var recordId = ev.Id.ToString();
            if (_gate is null)
            {
                _logger.LogWarning(
                    "KgCalendarDevIndexer: authorization gate unavailable; skipping record {RecordId}.", recordId);
                continue;
            }

            var at = _time.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(DevIndexerPrincipal), tenantId, at);
            var decision = await _gate.DecideAsync(
                authority.Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
                    "record",
                    recordId),
                ct).ConfigureAwait(false);
            if (decision.Verdict is AuthorizationVerdict.Denied)
            {
                _logger.LogWarning(
                    "KgCalendarDevIndexer: {Principal} has no records:write grant for {RecordId}; skipping.",
                    DevIndexerPrincipal,
                    recordId);
                continue;
            }

            await _indexer.IndexNodeAsync(
                new SearchNodeRow
                {
                    RecordId = recordId,
                    TenantId = tenantId.Value,
                    NodeType = CalendarEventNodeType,
                    Title = ev.Title,
                    Body = BuildSearchBody(ev),
                    Residency = SearchResidency.Cache,
                },
                decision,
                ct).ConfigureAwait(false);
            recordIds.Add(recordId);
        }

        _logger.LogInformation(
            "KgCalendarDevIndexer: indexed {Count} calendar event(s) for tenant {TenantId} into the KG "
            + "search index (NodeType={NodeType}).",
            recordIds.Count, tenantId, CalendarEventNodeType);

        return recordIds;
    }

    /// <summary>
    /// Seeds the resolved OS-user principal an explicit, active, cache-resident
    /// record-scoped grants naming EXACTLY the indexed event ids — through the grant
    /// store, so the fail-closed clip authorizes those records for that principal (the load-bearing security
    /// step). Idempotent: skips when the principal already holds an active grant covering the events.
    /// </summary>
    private async Task SeedPrincipalGrantAsync(
        TenantId tenantId, IReadOnlyList<string> recordIds, CancellationToken ct)
    {
        if (recordIds.Count == 0)
        {
            return;
        }

        // PINNED footgun: the grant's principal id MUST be the SAME string the KG search route resolves +
        // passes to SearchAsync. Both read the current node's roster edge by signing key — one source of truth.
        var principalId = CurrentPrincipalSignatureRoutes.ResolveRosterPrincipal(_roster, _nodeSigner);

        // Idempotency: if an active grant for this principal already reaches every seeded event, do nothing.
        var existing = await _grantStore
            .FindByPrincipalAsync(tenantId, principalId, ct)
            .ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (recordIds.All(id => existing.Any(g => g.IsActiveAt(now)
            && g.Scope.Contains(ScopeExpression.Parse($"/records/{id}")))))
        {
            _logger.LogDebug(
                "KgCalendarDevIndexer: principal {Principal} already holds an active grant covering the "
                + "seeded events for tenant {TenantId} — grant seed is a no-op (idempotent).",
                principalId.Value, tenantId);
            return;
        }

        foreach (var recordId in recordIds)
        {
            var grant = new AccessGrant(GrantId.New(), tenantId, principalId,
                AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse($"/records/{recordId}"),
                GrantResidency.Cache, new GrantValidity(now.AddMinutes(-1)), GranterKind.Person,
                SeedGranter, now, new GrantProvenance(GrantSourceKind.Manual,
                    new GrantReason(GrantReasonCodes.Manual, "kg-calendar-demo"), SeedGranter), now);
            var sourceRef = $"kg-cal-search-demo-grant:{tenantId}:{principalId.Value}:{recordId}";
            await _grantStore.AppendAsync(tenantId, grant, sourceRef, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "KgCalendarDevIndexer: seeded a ForRecords access-grant for principal {Principal} over {Count} "
            + "calendar-event record(s) in tenant {TenantId} (residency=Cache, active) — the KG clip now "
            + "returns those events for that principal, and ONLY that principal.",
            principalId.Value, recordIds.Count, tenantId);
    }

    /// <summary>
    /// Builds the searchable <see cref="SearchNodeRow.Body"/> for a calendar event. Concatenates the
    /// humanized resource display (so <c>Dr. Smith</c> hits — the seeded resource is the raw id
    /// <c>party-dr-smith</c>), the participant id values, the description, and the occupancy classification.
    /// The title is indexed separately (it covers <c>stand-up</c> / <c>consult</c>); the body covers the
    /// resource/participant text the title omits.
    /// </summary>
    internal static string BuildSearchBody(CalendarEvent ev)
    {
        var parts = new List<string>();

        if (ev.ResourceRef is { } resource)
        {
            // Both the raw id (party-dr-smith — substring "Smith" matches via trigram) AND a humanized form
            // ("Dr. Smith" — the natural acceptance term, which has a space + period the raw id lacks).
            parts.Add(resource.Value);
            parts.Add(Humanize(resource.Value));
        }

        foreach (var participation in ev.Participations)
        {
            var value = participation.Participant.Value;
            parts.Add(value);
            parts.Add(Humanize(value));
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            parts.Add(ev.Description!);
        }

        parts.Add(ev.Occupancy.ToString());

        // De-duplicate (resource + its participation are the same party) while preserving order.
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var ordered = parts.Where(p => !string.IsNullOrWhiteSpace(p) && seen.Add(p));
        return string.Join(" ", ordered);
    }

    /// <summary>
    /// Humanizes a raw participant id (<c>party-dr-smith</c> → <c>dr smith</c> with a "Dr." expansion) so a
    /// natural-language search term like <c>Dr. Smith</c> matches. Strips a leading <c>party-</c>/<c>asset-</c>
    /// prefix, splits on hyphens, expands the <c>dr</c> token to <c>Dr.</c>, and title-cases the rest.
    /// </summary>
    private static string Humanize(string rawId)
    {
        var trimmed = rawId;
        foreach (var prefix in new[] { "party-", "asset-" })
        {
            if (trimmed.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        var tokens = trimmed.Split('-', System.StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            if (token.Equals("dr", System.StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("Dr.");
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(token.ToLowerInvariant()));
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
