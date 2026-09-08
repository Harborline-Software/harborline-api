using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
#pragma warning disable HARBORLINE_API_PROVNEUT_001 // Ticket 026: explicit waiver for the existing cross-context enlistment until the atomic write scope moves wholly behind Data.
using IDbContextTransaction = Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction;
#pragma warning restore HARBORLINE_API_PROVNEUT_001

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.People.Foundation.Validation;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local contacts surface — the T2b contacts node-flip (ADR 0113 ABSOLUTE local-first). Drives the
/// ContactsPage / ContactDetailPage fully offline: a single-device install (signal-bridge STOPPED) lists,
/// views, creates, and updates contacts against the recoverable keyed (SQLCipher) store.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns contacts data.</b> Reads + writes go through <see cref="NodeEfPartyRepository"/>
/// (over the recoverable <see cref="Data.LocalNodeDbContext"/>; Party + contact-history tables mapped by
/// the shared <c>PeopleEntityModule</c> — the tables already exist in the node <c>InitialSchema</c>
/// migration, so this flip adds NO new schema). Contacts are TENANT-WIDE (the People read model has no
/// entity dimension), so there is no entity header; the node serves the single install tenant.
/// <list type="bullet">
///   <item><c>GET  /api/local-node/contacts</c> — list live contacts (optional <c>?role=</c> filter).</item>
///   <item><c>GET  /api/local-node/contacts/{id}</c> — full detail incl. active emails/phones/addresses/roles;
///     opaque 404 if absent.</item>
///   <item><c>POST /api/local-node/contacts</c> — create a contact. 201 Created.</item>
///   <item><c>POST /api/local-node/contacts/{id}/update</c> — patch mutable fields (caller-owns-state).</item>
///   <item><c>POST /api/local-node/contacts/{id}/delete</c> AND <c>DELETE /api/local-node/contacts/{id}</c> —
///     ARCHIVE (soft-delete / tombstone). Stamps <c>DeletedAt</c>; the contact then hides from list/get and
///     the tombstone projects into the CRDT as a value-change that syncs. NOT a hard row-DELETE.</item>
/// </list>
/// </para>
/// <para>
/// <b>Wire shape == the Bridge contract.</b> These DTOs are field-identical to the Bridge
/// <c>ContactsEndpoints</c> DTOs (camelCase: <c>contactId</c>, <c>displayName</c>, …). The frontend client
/// repoints IN PLACE (the node base URL replaces <c>/api/v1</c>) with no field remapping — the existing
/// frontend types match verbatim.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0092).</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), not a
/// fixed <c>"local"</c> sentinel; the write actor is the session-resolved caller party
/// (<see cref="NodeCallerParty"/>), falling back to the single operator when no principal is bound.
/// Detail returns opaque 404 when the row's tenant differs — the per-org isolation predicate, so
/// switching the active org switches which org's contacts are visible. The cross-tenant audit-trail
/// emission the Bridge does is unnecessary on the loopback node.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only Kestrel listener (the Bridge CSRF +
/// AuthenticatedTenantPolicy posture does not apply on the node).
/// </para>
/// <para>
/// <b>Wiring.</b> The repository accessor is injected from the OUTER host container and passed to
/// <see cref="Map"/> as a closed-over dependency — NOT resolved via <c>[FromServices]</c> (bug-2849).
/// </para>
/// </remarks>
public static class ContactRoutes
{
    /// <summary>Canonical route base for the node-local contacts surface.</summary>
    public const string RouteBase = "/api/local-node/contacts";



    /// <summary>
    /// Maps the contacts routes onto <paramref name="app"/>, closing over the party repository accessor and
    /// the contacts CRDT projection (multi-device INC-4). After every successful create/update/delete EF
    /// write, the route projects the contact into <paramref name="crdt"/>, which emits a sync delta and
    /// (on inbound peer deltas) merges converged state back into the EF store the GET routes read.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        TimeProvider timeProvider)
    {
        System.ArgumentNullException.ThrowIfNull(app);
        System.ArgumentNullException.ThrowIfNull(parties);
        System.ArgumentNullException.ThrowIfNull(crdt);

        MapList(app, parties, activeTeam);
        MapDetail(app, parties, activeTeam);
        MapCreate(app, parties, crdt, activeTeam, identityFactory, timeProvider);
        MapUpdate(app, parties, crdt, activeTeam, timeProvider);
        MapRoles(app, parties, activeTeam, timeProvider);
        MapDelete(app, parties, crdt, activeTeam, timeProvider);
    }

    private static void MapRoles(IEndpointRouteBuilder app, NodeEfPartyRepository parties,
        IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/roles", async (string id, AttachRoleBody body, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.ContactsWrite, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var partyId = new PartyId(id);
            var party = await parties.GetByIdAsync(partyId, ct).ConfigureAwait(false);
            if (party is null || party.TenantId != tenant) return Results.NotFound();
            if (body is null || string.IsNullOrWhiteSpace(body.RoleName))
                return Results.BadRequest(new { error = "role_name_required" });
            try
            {
                var code = body.RoleName.Trim().ToLowerInvariant();
                var role = await parties.AttachRoleAsync(
                    partyId, code, $"party-profile:{code}", NodeCallerParty.Resolve(http), admittedAt, ct)
                    .ConfigureAwait(false);
                return Results.Ok(new ContactRoleWire(
                    role.Id.Value, role.RoleName, role.RoleRecordId, role.StartedAt.ToString()));
            }
            catch (PartyValidationException ex)
            {
                return Results.BadRequest(new { error = "validation_failed",
                    detail = ex.Result.Errors.Count > 0 ? ex.Result.Errors[0] : "validation_failed" });
            }
        });

        app.MapDelete($"{RouteBase}/{{id}}/roles/{{roleId}}", async (
            string id, string roleId, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.ContactsWrite, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var partyId = new PartyId(id);
            var party = await parties.GetByIdAsync(partyId, ct).ConfigureAwait(false);
            if (party is null || party.TenantId != tenant) return Results.NotFound();
            var active = await parties.GetActiveRolesAsync(partyId, ct).ConfigureAwait(false);
            if (!active.Any(role => role.Id.Value == roleId)) return Results.NotFound();
            await parties.DetachRoleAsync(
                new PartyRoleId(roleId), endedReason: null, NodeCallerParty.Resolve(http), admittedAt, ct)
                .ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // ── GET /api/local-node/contacts ───────────────────────────────────────────
    private static void MapList(IEndpointRouteBuilder app, NodeEfPartyRepository parties, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (string? role, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, Permission.ContactsRead, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var repo = parties;
            var rows = await repo.ListByTenantAsync(LocalTenantId, ct).ConfigureAwait(false);

            IEnumerable<Party> filtered = rows;
            if (role is not null)
            {
                var roleNormalized = role.Trim().ToLowerInvariant();
                var withRole = new List<Party>();
                foreach (var p in rows)
                {
                    if (await repo.HasActiveRoleAsync(p.Id, roleNormalized, ct).ConfigureAwait(false))
                        withRole.Add(p);
                }
                filtered = withRole;
            }

            return Results.Ok(new ContactListResponse(filtered.Select(ToSummary).ToArray()));
        });
    }

    // ── GET /api/local-node/contacts/{id} ──────────────────────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeEfPartyRepository parties, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, Permission.ContactsRead, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var repo = parties;
            var partyId = new PartyId(id);
            var party = await repo.GetByIdAsync(partyId, ct).ConfigureAwait(false);
            if (party is null || party.TenantId != LocalTenantId)
                return Results.NotFound();

            var emails    = await repo.GetActiveEmailsAsync(partyId, ct).ConfigureAwait(false);
            var phones    = await repo.GetActivePhonesAsync(partyId, ct).ConfigureAwait(false);
            var addresses = await repo.GetActiveAddressesAsync(partyId, ct).ConfigureAwait(false);
            var roles     = await repo.GetActiveRolesAsync(partyId, ct).ConfigureAwait(false);

            return Results.Ok(ToDetail(party, emails, phones, addresses, roles));
        });
    }

    // ── POST /api/local-node/contacts ──────────────────────────────────────────
    private static void MapCreate(
        IEndpointRouteBuilder app,
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateContactBody body, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, Permission.ContactsCreate, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "display_name_required" });
            if (!TryParseKind(body.Kind, out var kind))
                return Results.BadRequest(new { error = "invalid_kind",
                    detail = $"Unknown kind '{body.Kind}'. Expected 'person' or 'organization'." });

            Party party;
            try
            {
                party = await parties
                    .CreateInTransactionAsync(
                        LocalTenantId,
                        kind,
                        body.DisplayName.Trim(),
                        NodeCallerParty.Resolve(http),
                        admittedAt,
                        (partyContext, created, transaction, cancellationToken) =>
                            AppendContactAuditAsync(
                                identityFactory,
                                partyContext,
                                transaction,
                                created,
                                http,
                                admittedAt,
                                cancellationToken),
                        cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (PartyValidationException ex)
            {
                return Results.BadRequest(new { error = "validation_failed",
                    detail = ex.Result.Errors.Count > 0 ? ex.Result.Errors[0] : "validation_failed" });
            }
            // INC-4: project the just-persisted contact into the CRDT (EF-first, then project) so the edit
            // becomes a sync delta. Convergence target = the contacts grid's core fields.
            crdt.ProjectUpsert(party);

            var detail = ToDetail(party,
                Array.Empty<EmailAddress>(), Array.Empty<PhoneNumber>(),
                Array.Empty<PartyAddress>(), Array.Empty<PartyRole>());
            return Results.Created($"{RouteBase}/{party.Id.Value}", detail);
        });
    }

    private static async Task AppendContactAuditAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        Data.LocalNodeDbContext partyContext,
        IDbContextTransaction partyTransaction,
        Party party,
        HttpContext http,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken)
    {
        await using var identity = await identityFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Production composes both contexts over the same encrypted local-node.db. The identity
        // context reads and writes the current chain through the party context's transaction, so
        // the party row, audit envelope, and hash-chain head have one atomic commit.
        var identityConnection = identity.Database.GetDbConnection();
        var partyConnection = partyContext.Database.GetDbConnection();
        var identityDataSource = identityConnection.DataSource;
        var partyDataSource = partyConnection.DataSource;
        var sharesPartyStore =
            !string.Equals(identityDataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(
                    Path.GetFullPath(identityDataSource),
                    Path.GetFullPath(partyDataSource),
                    StringComparison.Ordinal) ||
                string.Equals(
                    CanonicalDataSource(identityDataSource),
                    CanonicalDataSource(partyDataSource),
                    StringComparison.Ordinal));
        if (!sharesPartyStore)
            throw new InvalidOperationException("Contact audit and party stores must be the same database.");

        // Rebind the identity context to the party connection and enlist it in the transaction
        // already opened by the repository so its SaveChanges participates in the same commit.
        identity.Database.SetDbConnection(partyConnection, contextOwnsConnection: false);
        identity.Database.UseTransaction(
            Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions
                .GetDbTransaction(partyTransaction));

        var contactId = party.Id.Value;
        var actorId = NodeCallerParty.Resolve(http).Value;
        var correlationId = $"party-created:{contactId}";
        var commandFingerprint = InstallationAuditIntegrity.Hash(
            "people-party-created-command-v1", contactId, party.DisplayName, party.Kind.ToString());
        var payloadDigest = InstallationAuditIntegrity.Hash(
            "people-party-created-payload-v1", contactId, party.DisplayName, party.Kind.ToString());
        await InstallationIdentityAuditChain.AppendEventAsync(
            identity,
            InstallationIdentityAuditEventTypes.ContactCreated,
            "party",
            actorId,
            correlationId,
            commandFingerprint,
            payloadDigest,
            admittedAt,
            cancellationToken).ConfigureAwait(false);

        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CanonicalDataSource(string dataSource)
    {
        if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            return dataSource;

        try
        {
            var fullPath = Path.GetFullPath(dataSource);
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            var current = root;
            foreach (var component in fullPath[root.Length..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                var resolved = Directory.Exists(current)
                    ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    : new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (resolved is not null)
                    current = resolved;
            }

            return current;
        }
        catch (IOException)
        {
            return Path.GetFullPath(dataSource);
        }
    }

    // ── POST /api/local-node/contacts/{id}/update ───────────────────────────────
    private static void MapUpdate(
        IEndpointRouteBuilder app,
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/update", async (string id, UpdateContactBody body, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, Permission.ContactsWrite, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var repo = parties;
            var partyId = new PartyId(id);
            var existing = await repo.GetByIdAsync(partyId, ct).ConfigureAwait(false);
            if (existing is null || existing.TenantId != LocalTenantId)
                return Results.NotFound();

            var updated = existing with
            {
                DisplayName  = body.DisplayName?.Trim() ?? existing.DisplayName,
                LegalName    = body.LegalName    ?? existing.LegalName,
                Notes        = body.Notes        ?? existing.Notes,
                DoNotContact = body.DoNotContact ?? existing.DoNotContact,
                DoNotEmail   = body.DoNotEmail   ?? existing.DoNotEmail,
                DoNotCall    = body.DoNotCall    ?? existing.DoNotCall,
                DoNotSms     = body.DoNotSms     ?? existing.DoNotSms,
            };

            Party persisted;
            try
            {
                persisted = await repo.UpdateAsync(updated, NodeCallerParty.Resolve(http), admittedAt, ct)
                    .ConfigureAwait(false);
            }
            catch (PartyValidationException ex)
            {
                return Results.BadRequest(new { error = "validation_failed",
                    detail = ex.Result.Errors.Count > 0 ? ex.Result.Errors[0] : "validation_failed" });
            }

            // INC-4: project the updated contact into the CRDT so the edit becomes a sync delta.
            crdt.ProjectUpsert(persisted);

            var emails    = await repo.GetActiveEmailsAsync(partyId, ct).ConfigureAwait(false);
            var phones    = await repo.GetActivePhonesAsync(partyId, ct).ConfigureAwait(false);
            var addresses = await repo.GetActiveAddressesAsync(partyId, ct).ConfigureAwait(false);
            var roles     = await repo.GetActiveRolesAsync(partyId, ct).ConfigureAwait(false);
            return Results.Ok(ToDetail(persisted, emails, phones, addresses, roles));
        });
    }

    // ── POST /api/local-node/contacts/{id}/delete  AND  DELETE /api/local-node/contacts/{id} ────
    private static void MapDelete(
        IEndpointRouteBuilder app,
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        // ARCHIVE (soft-delete / tombstone), NOT a hard row-DELETE. Contacts are a master/Party, so per the
        // CIC MVP delete-semantics ruling (2026-06-03) they archive — DeleteAsync stamps DeletedAt (the
        // tombstone), bumps Version, and leaves the row in place; list/get then exclude it (DeletedAt == null).
        // After the EF write commits we project the tombstone into the CRDT (ProjectDelete = Set(Deleted=true),
        // a VALUE change — the earlier repository ticket #1260 F1 trigger), so the deletion becomes a sync delta that converges
        // to peers exactly as the multi-device harness proved. Both verbs map to the same handler: the live
        // 3-way-test probe found DELETE → 405 and POST .../delete → 404 (neither existed), and the other node
        // routes use POST .../<verb> while the Bridge/REST clients expect DELETE — so we serve both.
        var handler = DeleteHandler(parties, crdt, activeTeam, timeProvider);
        app.MapPost($"{RouteBase}/{{id}}/delete", handler);
        app.MapDelete($"{RouteBase}/{{id}}", handler);
    }

    private static Func<string, HttpContext, CancellationToken, Task<IResult>> DeleteHandler(
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider) =>
        async (string id, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, Permission.ContactsArchive, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var repo = parties;
            var partyId = new PartyId(id);

            // Opaque 404 when the contact is absent, already-tombstoned, or belongs to another tenant —
            // mirrors the detail route. GetByIdAsync returns null for a tombstoned row, so a second delete is
            // a 404 (the row is already archived), matching the "hidden from get" semantics.
            var existing = await repo.GetByIdAsync(partyId, ct).ConfigureAwait(false);
            if (existing is null || existing.TenantId != LocalTenantId)
                return Results.NotFound();

            Party tombstoned;
            try
            {
                // Soft-delete: stamps DeletedAt/DeletedBy, bumps Version, leaves the row in place.
                tombstoned = await repo.DeleteAsync(
                    partyId, reason: null, NodeCallerParty.Resolve(http), admittedAt, ct).ConfigureAwait(false);
            }
            catch (PartyValidationException ex)
            {
                return Results.BadRequest(new { error = "validation_failed",
                    detail = ex.Result.Errors.Count > 0 ? ex.Result.Errors[0] : "validation_failed" });
            }

            // INC-4: project the tombstone into the CRDT so the archive becomes a sync delta (value-change,
            // not key-removal — converges as a tombstone the peer reconciles, never a key-resurrection).
            crdt.ProjectDelete(tombstoned);

            return Results.NoContent();
        };

    // ── Request bodies ───────────────────────────────────────────────────────────
    public sealed record CreateContactBody(string? DisplayName, string? Kind);
    public sealed record AttachRoleBody(string? RoleName);
    public sealed record UpdateContactBody(
        string? DisplayName, string? LegalName, string? Notes,
        bool? DoNotContact, bool? DoNotEmail, bool? DoNotCall, bool? DoNotSms);

    // ── Response DTOs (field-identical to the Bridge contract) ─────────────────────
    public sealed record ContactListResponse(ContactSummaryWire[] Contacts);

    public sealed record ContactSummaryWire(
        string ContactId, string DisplayName, string? LegalName, string Kind, bool DoNotContact);

    public sealed record ContactDetailWire(
        string ContactId, string DisplayName, string? LegalName, string Kind,
        string? Notes, string? WebSite, string[] Tags,
        bool DoNotContact, bool DoNotEmail, bool DoNotCall, bool DoNotSms,
        ContactEmailWire[] Emails, ContactPhoneWire[] Phones,
        ContactAddressWire[] Addresses, ContactRoleWire[] Roles,
        string CreatedAt, string UpdatedAt, long Version);

    public sealed record ContactEmailWire(string EmailId, string Address, bool IsPrimary, string? Label);
    public sealed record ContactPhoneWire(string PhoneId, string E164, bool IsPrimary, string? Label, bool IsMobile);
    public sealed record ContactAddressWire(
        string AddressId, string Line1, string? Line2, string City, string Region,
        string PostalCode, string Country, bool IsPrimary, string? Label);
    public sealed record ContactRoleWire(string RoleId, string RoleName, string RoleRecordId, string StartedAt);

    // ── Mapping helpers ────────────────────────────────────────────────────────────
    private static ContactSummaryWire ToSummary(Party p) => new(
        ContactId:    p.Id.Value,
        DisplayName:  p.DisplayName,
        LegalName:    p.LegalName,
        Kind:         p.Kind.ToString().ToLowerInvariant(),
        DoNotContact: p.DoNotContact);

    private static ContactDetailWire ToDetail(
        Party p,
        IReadOnlyList<EmailAddress> emails,
        IReadOnlyList<PhoneNumber> phones,
        IReadOnlyList<PartyAddress> addresses,
        IReadOnlyList<PartyRole> roles) => new(
        ContactId:    p.Id.Value,
        DisplayName:  p.DisplayName,
        LegalName:    p.LegalName,
        Kind:         p.Kind.ToString().ToLowerInvariant(),
        Notes:        p.Notes,
        WebSite:      p.WebSite,
        Tags:         p.Tags.ToArray(),
        DoNotContact: p.DoNotContact,
        DoNotEmail:   p.DoNotEmail,
        DoNotCall:    p.DoNotCall,
        DoNotSms:     p.DoNotSms,
        Emails:    emails.Select(e => new ContactEmailWire(e.Id.Value, e.Address, e.IsPrimary, e.Label)).ToArray(),
        Phones:    phones.Select(ph => new ContactPhoneWire(ph.Id.Value, ph.E164, ph.IsPrimary, ph.Label, ph.IsMobile)).ToArray(),
        Addresses: addresses.Select(a => new ContactAddressWire(
                       a.Id.Value, a.Address.Line1, a.Address.Line2, a.Address.City, a.Address.Region,
                       a.Address.PostalCode, a.Address.Country, a.IsPrimary, a.Label)).ToArray(),
        Roles:     roles.Select(r => new ContactRoleWire(r.Id.Value, r.RoleName, r.RoleRecordId, r.StartedAt.ToString())).ToArray(),
        CreatedAt: p.CreatedAt.ToString(),
        UpdatedAt: p.UpdatedAt.ToString(),
        Version:   p.Version);

    private static bool TryParseKind(string? raw, out PartyKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "person":       kind = PartyKind.Person;       return true;
            case "organization": kind = PartyKind.Organization; return true;
            default:             kind = default;                 return false;
        }
    }
}
