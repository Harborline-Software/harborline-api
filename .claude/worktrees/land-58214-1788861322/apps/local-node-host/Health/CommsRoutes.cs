using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local comms surface — the FIRST messaging doctype on the live node-host path. A team-wide,
/// append-only, ordered message log built on the ADR 0032 identity foundation. Mirrors
/// <see cref="ContactRoutes"/> structurally but the underlying CRDT is a list (append-log), not a map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conversation-addressed (C1).</b> The surface is now a SET of conversations (the design's "a comms
/// surface = a set of conversations"). The team channel is the well-known conversation
/// <see cref="CommsConversation.TeamConversationId"/> (<c>"team"</c>); a 1:1 DM (C2+) is a
/// <c>dm:</c>-prefixed conversation. Routes:
/// <list type="bullet">
///   <item><c>GET/POST /api/local-node/comms</c> — the BARE route (back-compat) — defaults to the
///     <c>"team"</c> conversation, so the current Harborline App + the in-flight team-channel GUI test keep
///     working unchanged.</item>
///   <item><c>GET/POST /api/local-node/comms/{conversationId}</c> — the conversation-addressed route —
///     reads/appends the named conversation (C1 ships the team channel via either form; DM ids become live
///     in C2+).</item>
/// </list>
/// </para>
/// <para>
/// <list type="bullet">
///   <item><c>POST</c> — append a message. The author is the ACTIVE ENROLLED MEMBER — the trust-roster party
///     bound to the signing principal key (gap #2). The message is SIGNED with the node's canonical principal
///     identity over the canonical signable form (which now includes the conversation id, so a message can't
///     be replayed into another thread). 201 Created.</item>
///   <item><c>GET</c> — the ordered log for the active team's named conversation (authored order).</item>
/// </list>
/// </para>
/// <para>
/// <b>Append-only — no edit/delete in the pilot.</b> A message is immutable once appended; there is no
/// update or delete route. Append-only is the point (it is also the GL's shape).
/// </para>
/// <para>
/// <b>Team-scoped (ADR 0032).</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>); a different active team is a
/// different comms log. Conversation scope (the thread within the team) is orthogonal to and below team
/// scope (which org).
/// </para>
/// <para>
/// <b>The CRDT write path.</b> After the append's EF write commits (EF-first), the route pushes the signed
/// message onto the conversation's CRDT list (<see cref="CommsCrdtProjection.AppendLocal"/>), which emits a
/// sync delta and (on inbound peer deltas) reconciles converged messages back into the EF read model the GET
/// reads. The recoverable <c>local-node.db</c> is the CRDT's ONLY durable sink (SC4-C2).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> caller-auth IS enforced — the LISTENER-LEVEL middleware gates every
/// non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF
/// stays N/A (explicit bearer, no cookie/ambient auth).
/// </para>
/// <para>
/// <b>Wiring.</b> The conversation registry (or a single projection, for tests) + signer + active-team
/// accessor are injected from the OUTER host container and passed to <see cref="Map(IEndpointRouteBuilder,
/// CommsConversationRegistry, IOperationSigner, IActiveTeamAccessor, NodeCallerSessionToken, string)"/> as
/// closed-over dependencies — NOT resolved via <c>[FromServices]</c> (bug-2849).
/// </para>
/// </remarks>
public static class CommsRoutes
{
    /// <summary>Canonical route base for the node-local comms surface.</summary>
    public const string RouteBase = "/api/local-node/comms";

    /// <summary>The conversation-addressed route — <c>/api/local-node/comms/{conversationId}</c>.</summary>
    public const string ConversationRoute = RouteBase + "/{conversationId}";

    /// <summary>
    /// The DM resolve-and-open route — <c>/api/local-node/comms/dm/{otherPartyId}</c> — gated by the shipped
    /// <see cref="CommsDmFeatureFlag"/> (default ON, kill-switch retained). Derives the deterministic <c>dm:</c>
    /// id from the active member + the target party (server-side; the no-coordination property), then
    /// reads/appends that DM thread (the body is SEALED at rest with the C5 roster-bound key). Mapped when the
    /// flag is enabled — which is the shipped default now that the DM body is encrypted end-to-end (C4 seal +
    /// C5 roster-bound, node-secret keys); the kill-switch removes ONLY this route, never team messaging.
    /// </summary>
    public const string DmResolveRoute = RouteBase + "/dm/{otherPartyId}";

    /// <summary>
    /// The DM roster route — <c>GET /api/local-node/comms/dm/roster</c> — returns the enrolled team members
    /// (excluding self) so the Harborline App's roster picker shows REAL enrolled peers rather than mock contacts.
    /// Gated by <see cref="CommsDmFeatureFlag"/> (default ON). Each entry carries the member's party id and a
    /// derived display name (the OS-username portion of the <c>os:&lt;user&gt;#&lt;key8&gt;</c> party id,
    /// or a truncated fallback for non-OS-pattern ids). Caller-auth enforced (same guard as the other DM routes).
    /// </summary>
    public const string DmRosterRoute = RouteBase + "/dm/roster";

    /// <summary>
    /// The author party id stamped when no enrolled-member id is supplied (the dev / single-host overloads
    /// that do not wire the trust roster). Matches the install-constant single-office operator.
    /// </summary>
    private const string FallbackAuthorPartyId = ActiveTeamAuthorizationContext.LocalUserId;

    // ── SINGLE-PROJECTION overloads (tests / dev) — map the bare + conversation routes scoped to ONE ─────────
    //    conversation (the projection's). The conversation-addressed route serves ONLY that conversation's id;
    //    any other id 404s. The SHIPPING host uses the registry overloads below for full conversation reach.

    /// <summary>
    /// Maps the comms routes over a SINGLE projection (the team channel, in tests). Uses the
    /// <see cref="FallbackAuthorPartyId"/> author — for dev / single-host call-sites that do not wire the trust
    /// roster. The SHIPPING host uses the registry overload.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsCrdtProjection comms,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
        => Map(app, comms, signer, activeTeam, new NodeCallerSessionToken(null), FallbackAuthorPartyId, timeProvider);

    /// <summary>
    /// Maps the comms routes over a SINGLE projection with the inc-4 caller-auth guard. Uses the
    /// <see cref="FallbackAuthorPartyId"/> author.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsCrdtProjection comms,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider)
        => Map(app, comms, signer, activeTeam, callerAuth, FallbackAuthorPartyId, timeProvider);

    /// <summary>
    /// Maps the comms routes over a SINGLE projection stamping <paramref name="activeMemberPartyId"/> as the
    /// author (the gap #2 shape). The conversation-addressed route resolves ONLY this projection's
    /// conversation; the bare route targets it too.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsCrdtProjection comms,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(comms);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMemberPartyId);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // A single-projection resolver: the bare route + this projection's own conversation id resolve to it;
        // any other conversation id is unknown (404) — tests exercise exactly one conversation.
        CommsCrdtProjection? Resolve(string? conversationId)
        {
            var id = CommsConversation.Normalize(conversationId);
            return string.Equals(id, comms.ConversationId, StringComparison.Ordinal) ? comms : null;
        }

        MapRoutes(app, Resolve, signer, activeTeam, callerAuth, activeMemberPartyId, timeProvider);
    }

    // ── REGISTRY overloads (SHIPPING) — full conversation addressing over the lazy conversation registry. ────

    /// <summary>
    /// Maps the comms routes over the conversation REGISTRY (the SHIPPING path) — full conversation
    /// addressing. The bare route targets the team channel; a conversation-addressed route resolves (creating
    /// on first use) the named conversation. Stamps <paramref name="activeMemberPartyId"/> as the author.
    /// </summary>
    /// <param name="conversations">The lazy conversation registry (one projection per conversation).</param>
    /// <param name="activeMemberPartyId">
    /// The active enrolled member's party id (the roster party bound to <paramref name="signer"/>'s key).
    /// </param>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsConversationRegistry conversations,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        TimeProvider timeProvider)
        => Map(app, conversations, signer, activeTeam, callerAuth, activeMemberPartyId, new CommsDmFeatureFlag(), timeProvider);

    /// <summary>
    /// Maps the comms routes over the conversation REGISTRY (the SHIPPING path) WITH the DM feature flag. The
    /// generic conversation-addressed routes (bare + <c>{conversationId}</c>) map unconditionally; the DM
    /// resolve-and-open route (<see cref="DmResolveRoute"/>) maps when <paramref name="dmFlag"/> is enabled —
    /// the shipped default now that the DM body is sealed end-to-end (C4 seal + C5 roster-bound keys). The
    /// kill-switch (flag disabled) removes ONLY the DM route; the team channel is unaffected.
    /// </summary>
    /// <param name="dmFlag">The shipped DM feature flag — when disabled (kill-switch), the DM route is NOT mapped.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsConversationRegistry conversations,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        CommsDmFeatureFlag dmFlag,
        TimeProvider timeProvider)
        => Map(app, conversations, signer, activeTeam, callerAuth, activeMemberPartyId, dmFlag, roster: null, timeProvider);

    /// <summary>
    /// Maps the comms routes over the conversation REGISTRY (the SHIPPING path) WITH the DM feature flag AND the
    /// enrolled-member roster. The DM roster route (<see cref="DmRosterRoute"/>) is mapped alongside the DM
    /// resolve-and-open route when <paramref name="dmFlag"/> is enabled; it returns the enrolled peers (excluding
    /// self) so the Harborline App's roster picker shows REAL enrolled members instead of mock contacts.
    /// </summary>
    /// <param name="dmFlag">The shipped DM feature flag.</param>
    /// <param name="roster">
    /// The live enrolled-member roster. When non-null and <paramref name="dmFlag"/> is enabled, the DM roster
    /// route is mapped. When null (legacy overload path), the roster route is skipped.
    /// </param>
    public static void Map(
        IEndpointRouteBuilder app,
        CommsConversationRegistry conversations,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        CommsDmFeatureFlag dmFlag,
        NodeTeamRoster? roster,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMemberPartyId);
        ArgumentNullException.ThrowIfNull(dmFlag);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // The registry resolver: any conversation id resolves (lazily creating the projection). C1 only the
        // team channel is exercised; DM ids become live in C2+. Never null (the registry always creates).
        CommsCrdtProjection? Resolve(string? conversationId) => conversations.GetOrCreate(conversationId);

        MapRoutes(app, Resolve, signer, activeTeam, callerAuth, activeMemberPartyId, timeProvider);

        // ── DM resolve-and-open route, gated by the shipped CommsDmFeatureFlag (default ON, kill-switch). ─────
        // The DM body is SEALED end-to-end (C4 seal + C5 roster-bound, node-secret keys) — confidentiality rests
        // on the crypto, NOT on this flag — so the surface ships ON by default. The kill-switch (flag disabled)
        // removes ONLY this route for an incident, WITHOUT a redeploy and without touching the team channel.
        if (dmFlag.IsEnabled)
        {
            // C4 — the DM resolve route resolves a KEYED projection via the registry (GetOrCreateDm with the
            // participant pair), so the body is SEALED on append + UNSEALED on read for the participant. The
            // active member is ALWAYS the authenticated actor (no-impersonation); the DM is between the caller +
            // the target — the participant set IS the v1 ACL (design §2.4).
            // GET: read the DM thread between the active member + {otherPartyId} (derive the dm: id server-side).
            app.MapGet(DmResolveRoute, (string otherPartyId, HttpRequest httpRequest, CancellationToken ct) =>
                HandleDmListAsync(otherPartyId, conversations, activeTeam, callerAuth, activeMemberPartyId, httpRequest, ct));
            // POST: append to the DM thread between the active member + {otherPartyId}.
            app.MapPost(DmResolveRoute,
                (string otherPartyId, AppendMessageBody body, HttpRequest httpRequest, CancellationToken ct) =>
                    HandleDmAppendAsync(
                        otherPartyId, body, conversations, signer, activeTeam, callerAuth, activeMemberPartyId,
                        timeProvider, httpRequest, ct));

            // DM roster route — returns enrolled peers (excluding self) so the Harborline App picker shows REAL contacts.
            // Mapped only when the roster holder is wired (the SHIPPING path via HostedCommsApiEndpoint).
            if (roster is not null)
            {
                app.MapGet(DmRosterRoute, (HttpRequest httpRequest) =>
                    HandleDmRosterList(roster, callerAuth, activeMemberPartyId, httpRequest));
            }
        }
    }

    // ── Shared route mapping over a conversation resolver ────────────────────────────────────────────────────

    private static void MapRoutes(
        IEndpointRouteBuilder app,
        Func<string?, CommsCrdtProjection?> resolve,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        TimeProvider timeProvider)
    {
        // BARE routes (back-compat) — default to the team channel.
        app.MapGet(RouteBase, (HttpRequest httpRequest, CancellationToken ct) =>
            HandleListAsync(CommsConversation.TeamConversationId, resolve, activeTeam, callerAuth, httpRequest, ct));
        app.MapPost(RouteBase, (AppendMessageBody body, HttpRequest httpRequest, CancellationToken ct) =>
            HandleAppendAsync(
                CommsConversation.TeamConversationId, body, resolve, signer, activeTeam, callerAuth,
                activeMemberPartyId, timeProvider, httpRequest, ct));

        // CONVERSATION-ADDRESSED routes.
        app.MapGet(ConversationRoute, (string conversationId, HttpRequest httpRequest, CancellationToken ct) =>
            HandleListAsync(conversationId, resolve, activeTeam, callerAuth, httpRequest, ct));
        app.MapPost(ConversationRoute,
            (string conversationId, AppendMessageBody body, HttpRequest httpRequest, CancellationToken ct) =>
                HandleAppendAsync(
                    conversationId, body, resolve, signer, activeTeam, callerAuth, activeMemberPartyId,
                    timeProvider, httpRequest, ct));
    }

    // ── GET handler ─────────────────────────────────────────────────────────────
    private static async Task<IResult> HandleListAsync(
        string conversationId,
        Func<string?, CommsCrdtProjection?> resolve,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        // inc-4 CALLER AUTH (fail-closed): loopback bind ≠ caller trust. A stranger process without the
        // Harborline App's session token is rejected before any read.
        if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            return NodeCallerSessionToken.RejectResult();

        var comms = resolve(conversationId);
        if (comms is null)
            return Results.NotFound(new { error = "unknown_conversation" });

        var tenantId = NodeTenant.Resolve(activeTeam).Value;
        var log = await comms.ReadLogAsync(tenantId, ct).ConfigureAwait(false);
        return Results.Ok(new CommsLogResponse(log.Select(ToWire).ToArray()));
    }

    // ── POST handler ────────────────────────────────────────────────────────────
    private static async Task<IResult> HandleAppendAsync(
        string conversationId,
        AppendMessageBody body,
        Func<string?, CommsCrdtProjection?> resolve,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        // inc-4 CALLER AUTH (fail-closed): reject a stranger BEFORE any write/attribution.
        if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            return NodeCallerSessionToken.RejectResult();

        if (body is null || string.IsNullOrWhiteSpace(body.Body))
            return Results.BadRequest(new { error = "body_required" });

        var comms = resolve(conversationId);
        if (comms is null)
            return Results.NotFound(new { error = "unknown_conversation" });

        var tenantId = NodeTenant.Resolve(activeTeam).Value;
        var authoredAt = timeProvider.GetUtcNow();

        // Sign + attribute (gap #2): the message carries the ACTIVE ENROLLED MEMBER's party id + the author's
        // signed identity + the CONVERSATION id (in the signed payload — a message can't be replayed into
        // another thread). The author-party-id and the signing key are consistent so the production forge-proof
        // merge gate passes the operator's OWN messages.
        var message = await CommsMessageFactory
            .CreateSignedAsync(
                signer, activeMemberPartyId, tenantId, body.Body.Trim(), authoredAt,
                conversationId: comms.ConversationId, ct: ct)
            .ConfigureAwait(false);

        // EF-first (durable read model), then push onto the conversation's CRDT list so the append becomes a
        // sync delta.
        await comms.PersistLocalAsync(message, ct).ConfigureAwait(false);
        comms.AppendLocal(message);

        return Results.Created($"{RouteBase}/{comms.ConversationId}/{message.MessageId}", ToWire(message));
    }

    // ── C2 DM resolve-and-open handlers (DEV/TEST-GATED) ───────────────────────────
    //
    // Derive the deterministic dm: id server-side from {teamId, activeMember, otherParty} (the no-coordination
    // property — both ends compute the SAME id), then delegate to the generic conversation handlers. The
    // active member is ALWAYS the authenticated actor (no-impersonation): a DM is between the caller and the
    // target — there is no "read X's DM as X" path. The participant set IS the v1 ACL (design §2.4).

    private static async Task<IResult> HandleDmListAsync(
        string otherPartyId,
        CommsConversationRegistry conversations,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            return NodeCallerSessionToken.RejectResult();

        var conversationId = TryDeriveDmId(otherPartyId, activeTeam, activeMemberPartyId, out var error);
        if (conversationId is null)
            return Results.BadRequest(new { error });

        // C4 — resolve the KEYED DM projection (with the participant pair) so its sealed bodies UNSEAL for the
        // participant reader. Decryption happens IN THE NODE (DR-4); the plaintext returns over the caller-auth
        // loopback so the renderer stays node:-/key-free.
        var comms = conversations.GetOrCreateDm(conversationId, activeMemberPartyId, otherPartyId);
        var tenantId = NodeTenant.Resolve(activeTeam).Value;
        var log = await comms.ReadUnsealedLogAsync(tenantId, ct).ConfigureAwait(false);
        return Results.Ok(new CommsLogResponse(log.Select(ToWire).ToArray()));
    }

    private static async Task<IResult> HandleDmAppendAsync(
        string otherPartyId,
        AppendMessageBody body,
        CommsConversationRegistry conversations,
        IOperationSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            return NodeCallerSessionToken.RejectResult();

        if (body is null || string.IsNullOrWhiteSpace(body.Body))
            return Results.BadRequest(new { error = "body_required" });

        var conversationId = TryDeriveDmId(otherPartyId, activeTeam, activeMemberPartyId, out var error);
        if (conversationId is null)
            return Results.BadRequest(new { error });

        var comms = conversations.GetOrCreateDm(conversationId, activeMemberPartyId, otherPartyId);
        var tenantId = NodeTenant.Resolve(activeTeam).Value;
        var authoredAt = timeProvider.GetUtcNow();

        // C4 — the DM body is SEALED to the per-conversation key (sign-then-encrypt, DR-3): the signature covers
        // the PLAINTEXT, the stored/synced body is ciphertext. If this node cannot seal (not a participant / key
        // unresolvable) the append fails-closed rather than sending plaintext on the team-wide plane.
        if (!comms.CanSealConversation)
            return Results.Problem(
                detail: "dm_seal_unavailable: the per-conversation key could not be derived for this DM.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var message = await comms
            .CreateSignedSealedLocalAsync(signer, activeMemberPartyId, tenantId, body.Body.Trim(), authoredAt, ct)
            .ConfigureAwait(false);

        await comms.PersistLocalAsync(message, ct).ConfigureAwait(false);
        comms.AppendLocal(message);

        // Return the PLAINTEXT to the caller (the author) — it just wrote it — over the caller-auth loopback. The
        // stored + synced body remains the sealed ciphertext (only the wire to the renderer carries plaintext).
        var wire = ToWire(message) with { Body = body.Body.Trim() };
        return Results.Created($"{RouteBase}/{comms.ConversationId}/{message.MessageId}", wire);
    }

    // ── DM roster list handler ─────────────────────────────────────────────────

    /// <summary>
    /// GET /api/local-node/comms/dm/roster — the enrolled-member list for the DM picker (SHIPPING, DM_ENABLED).
    ///
    /// Returns the enrolled peers from the live <see cref="NodeTeamRoster"/>, EXCLUDING the active member (self).
    /// The Harborline App's roster picker calls this route so it shows REAL enrolled team members rather than the
    /// mock Bob/Cara placeholders. Caller-auth enforced (same guard as the other DM routes).
    ///
    /// Display name derivation: enrolled parties follow the <c>os:&lt;user&gt;#&lt;key8&gt;</c> genesis pattern.
    /// For those, the display name is the OS username extracted from the prefix (everything between the leading
    /// <c>os:</c> and the <c>#</c> separator). For non-OS-pattern ids, the first 12 chars are used as a fallback.
    /// No human-friendly name is persisted in the current substrate — the OS username is the closest honest label.
    /// </summary>
    private static IResult HandleDmRosterList(
        NodeTeamRoster roster,
        NodeCallerSessionToken callerAuth,
        string activeMemberPartyId,
        HttpRequest httpRequest)
    {
        if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            return NodeCallerSessionToken.RejectResult();

        var members = roster.Current.Members;
        var peers = new List<DmRosterMemberWire>(members.Count);
        foreach (var m in members)
        {
            // Exclude self — cannot DM yourself.
            if (string.Equals(m.PartyId, activeMemberPartyId, StringComparison.Ordinal))
                continue;

            peers.Add(new DmRosterMemberWire(m.PartyId, DeriveDisplayName(m.PartyId)));
        }

        return Results.Ok(peers);
    }

    /// <summary>
    /// Derive a human-readable display name from a party id.
    /// Genesis party ids follow the pattern <c>os:&lt;user&gt;#&lt;key8&gt;</c> (e.g. <c>os:chris#3a1b2c4d</c>).
    /// For those, extract the OS username between <c>"os:"</c> and <c>"#"</c>.
    /// For non-OS-pattern ids, truncate to 12 chars as a safe fallback.
    /// </summary>
    private static string DeriveDisplayName(string partyId)
    {
        const string OsPrefix = "os:";
        if (partyId.StartsWith(OsPrefix, StringComparison.Ordinal))
        {
            var rest = partyId.AsSpan(OsPrefix.Length); // "<user>#<key8>"
            var hashIdx = rest.IndexOf('#');
            if (hashIdx > 0)
            {
                return rest[..hashIdx].ToString(); // just the username
            }
        }
        // Fallback: truncate to 12 chars so the UI doesn't show a raw GUID.
        return partyId.Length <= 12 ? partyId : partyId[..12] + "…";
    }

    /// <summary>
    /// Derive the deterministic <c>dm:</c> id for the active member ↔ <paramref name="otherPartyId"/> within the
    /// active team. Returns null + an <paramref name="error"/> code on a bad request (empty / self-DM). The team
    /// id is the active-team-derived tenant (a DM is within the team's roster + salted by the team — design §1.3).
    /// </summary>
    private static string? TryDeriveDmId(
        string otherPartyId, IActiveTeamAccessor activeTeam, string activeMemberPartyId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(otherPartyId))
        {
            error = "other_party_required";
            return null;
        }
        if (string.Equals(otherPartyId, activeMemberPartyId, StringComparison.Ordinal))
        {
            error = "self_dm_not_allowed"; // a 1:1 DM requires two distinct participants.
            return null;
        }

        var teamId = NodeTenant.Resolve(activeTeam).Value;
        return DmConversationId.Derive(teamId, activeMemberPartyId, otherPartyId);
    }

    // ── Request body ─────────────────────────────────────────────────────────────
    /// <summary>POST body: the message text to append.</summary>
    public sealed record AppendMessageBody(string? Body);

    // ── DM roster wire shape — the Harborline App DM picker contract ─────────────────
    /// <summary>
    /// One entry in the <c>GET /api/local-node/comms/dm/roster</c> response.
    /// Matches the Harborline App <c>DmRosterMember</c> contract (<c>src/comms/commsClient.ts</c>).
    /// </summary>
    /// <param name="PartyId">The enrolled peer's party id.</param>
    /// <param name="DisplayName">Derived human-readable label (OS username from the party id pattern).</param>
    public sealed record DmRosterMemberWire(
        [property: System.Text.Json.Serialization.JsonPropertyName("partyId")] string PartyId,
        [property: System.Text.Json.Serialization.JsonPropertyName("displayName")] string DisplayName);

    // ── Response DTOs (camelCase wire by default JsonSerializerOptions) ────────────
    /// <summary>The ordered comms log response.</summary>
    public sealed record CommsLogResponse(MessageWire[] Messages);

    /// <summary>One message on the wire — includes the author attribution + the signature for verifiability.</summary>
    public sealed record MessageWire(
        string MessageId,
        string ConversationId,
        string AuthorPartyId,
        string AuthorIssuerId,
        string AuthoredAt,
        string Body,
        string Signature);

    private static MessageWire ToWire(MessageCrdtState m) => new(
        MessageId: m.MessageId,
        ConversationId: m.ConversationId,
        AuthorPartyId: m.AuthorPartyId,
        AuthorIssuerId: m.AuthorIssuerId,
        AuthoredAt: m.AuthoredAtIso,
        Body: m.Body,
        Signature: m.SignatureB64Url);
}
