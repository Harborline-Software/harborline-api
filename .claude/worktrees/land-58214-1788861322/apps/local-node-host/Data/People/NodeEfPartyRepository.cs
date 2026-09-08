using Microsoft.EntityFrameworkCore;
#pragma warning disable HARBORLINE_API_PROVNEUT_001 // Ticket 026: the node repository owns the SQLite transaction so the party row and identity audit commit atomically.
using IDbContextTransaction = Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction;
#pragma warning restore HARBORLINE_API_PROVNEUT_001

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Models.Events;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.People.Foundation.Validation;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Events;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>
/// Node-resident <see cref="IPartyReadModel"/> + <see cref="IPartyWriteService"/> over the recoverable,
/// Store-DEK-enveloped <see cref="LocalNodeDbContext"/> (ADR 0113 ABSOLUTE local-first; T2b contacts
/// node-flip).
///
/// <para>
/// A near-verbatim port of the Bridge <c>EfPartyRepository</c> with the SOLE difference being the
/// backing store (<see cref="LocalNodeDbContext"/> vs the Bridge Postgres context). The Party + contact
/// history tables (<c>parties</c>, <c>party_email_addresses</c>, <c>party_phone_numbers</c>,
/// <c>party_addresses</c>, <c>party_roles</c>) are mapped by the shared <c>PeopleEntityModule</c> and
/// already exist in the node <c>InitialSchema</c> migration — so this flip adds NO new schema.
/// </para>
///
/// <para>
/// <b>Tenant scoping (ADR 0092).</b> Read methods that accept a <c>TenantId</c> filter by explicit
/// TenantId; reads use <c>IgnoreQueryFilters()</c> (the node DbContext has no ambient-tenant global
/// filter) and apply the explicit predicate for defence-in-depth. Cross-tenant reads return null /
/// empty. Contacts are TENANT-WIDE (no entity dimension); the node serves the active-team-derived
/// tenant (<c>ActiveTeamTenantContext</c> / <c>NodeTenant.Resolve</c>; ADR 0032 identity layer), not a
/// fixed <c>"local"</c> — the explicit TenantId predicate is the per-org isolation boundary.
/// </para>
///
/// <para>
/// <b>Tombstone-not-delete + append-only contact history (CRDT §4 / no-hard-DELETE doctrine).</b>
/// <see cref="DeleteAsync"/> stamps <c>DeletedAt</c>; contact-history rows are insert-only with
/// supersedence. Identical to the Bridge repository.
/// </para>
///
/// <para>
/// <b>SC4-C2 recoverability.</b> Every persistence sink is the recoverable <c>local-node.db</c>; the
/// event publisher is the <see cref="NoopDomainEventPublisher"/> (the host never wires a cross-cluster
/// event bus), so no kernel CRDT-writer / per-team event log is reachable. SC4-T9(b) is unaffected.
/// </para>
/// </summary>
public sealed class NodeEfPartyRepository : IPartyReadModel, IPartyWriteService
{
    /// <summary>
    /// People-owned role edge used to bind an identity principal to a Party. The opaque role-record
    /// id is the principal user id; tenant and Party remain first-class columns.
    /// </summary>
    internal const string PrincipalUserBindingRoleName = "principal-user";

    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly IDomainEventPublisher _events;

    /// <summary>Construct bound to the node EF context factory and (no-op) domain event publisher.</summary>
    public NodeEfPartyRepository(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        TimeProvider time,
        IDomainEventPublisher? events = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ArgumentNullException.ThrowIfNull(time);
        _events = events ?? new NoopDomainEventPublisher();
    }

    // ──────────────────────────────────────────────────────────────────
    //  IPartyReadModel
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Party?> GetByIdAsync(PartyId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var party = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return party?.DeletedAt is not null ? null : party;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<PartyId, Party>> GetManyAsync(
        IReadOnlyCollection<PartyId> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return new Dictionary<PartyId, Party>();

        var idValues = ids.Select(i => i.Value).ToList();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => idValues.Contains(p.Id.Value) && p.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(p => p.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Party>> ListByTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves exactly one live People-owned principal binding under an explicit tenant. Missing,
    /// duplicate, detached, tombstoned, and wrong-tenant rows all refuse with null.
    /// </summary>
    internal async Task<Party?> ResolvePrincipalPartyAsync(
        TenantId tenantId,
        string principalUserId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default || string.IsNullOrWhiteSpace(principalUserId))
            return null;

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = await ctx.Set<PartyRole>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(role => role.TenantId == tenantId
                && role.RoleName == PrincipalUserBindingRoleName
                && role.RoleRecordId == principalUserId
                && role.EndedAt == null
                && role.DeletedAt == null)
            .Join(
                ctx.Set<Party>()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(party => party.TenantId == tenantId && party.DeletedAt == null),
                role => role.PartyId,
                party => party.Id,
                (_, party) => party)
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Party>> FindByExactDisplayNameAsync(
        TenantId tenantId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                && p.DeletedAt == null
                && p.DisplayName.ToLower() == displayName.ToLower())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Party>> FindByExactEmailAsync(
        TenantId tenantId,
        string emailAddress,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        var emailLower = emailAddress.ToLowerInvariant();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var partyIds = await ctx.Set<EmailAddress>()
            .AsNoTracking()
            .Where(ea => ea.TenantId == tenantId
                && ea.ReplacedAt == null
                && ea.DeletedAt == null
                && ea.Address.ToLower() == emailLower)
            .Select(ea => ea.PartyId.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (partyIds.Count == 0)
            return Array.Empty<Party>();

        return await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => partyIds.Contains(p.Id.Value) && p.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Party>> FindByExactPhoneE164Async(
        TenantId tenantId,
        string e164,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var partyIds = await ctx.Set<PhoneNumber>()
            .AsNoTracking()
            .Where(ph => ph.TenantId == tenantId
                && ph.ReplacedAt == null
                && ph.DeletedAt == null
                && ph.E164 == e164)
            .Select(ph => ph.PartyId.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (partyIds.Count == 0)
            return Array.Empty<Party>();

        return await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => partyIds.Contains(p.Id.Value) && p.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmailAddress>> GetActiveEmailsAsync(
        PartyId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<EmailAddress>()
            .AsNoTracking()
            .Where(ea => ea.PartyId == id && ea.ReplacedAt == null && ea.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PhoneNumber>> GetActivePhonesAsync(
        PartyId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PhoneNumber>()
            .AsNoTracking()
            .Where(pn => pn.PartyId == id && pn.ReplacedAt == null && pn.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PartyAddress>> GetActiveAddressesAsync(
        PartyId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PartyAddress>()
            .AsNoTracking()
            .Where(pa => pa.PartyId == id && pa.ReplacedAt == null && pa.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PartyRole>> GetActiveRolesAsync(
        PartyId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PartyRole>()
            .AsNoTracking()
            .Where(pr => pr.PartyId == id && pr.EndedAt == null && pr.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveRoleAsync(
        PartyId id,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PartyRole>()
            .AsNoTracking()
            .AnyAsync(pr => pr.PartyId == id
                && pr.RoleName == roleName
                && pr.EndedAt == null
                && pr.DeletedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────
    //  IPartyWriteService
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Party> CreateAsync(
        TenantId tenantId,
        PartyKind kind,
        string displayName,
        PartyId actor,
        DateTimeOffset at,
        PartyId? id = null,
        CancellationToken cancellationToken = default)
    {
        var party = Party.Create(tenantId, kind, displayName, actor, new Instant(at), id);
        Throw(PartyValidator.Validate(party));

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ctx.Set<Party>().Add(party);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await Publish(
            PeopleFoundationEventNames.PartyCreated,
            new PartyCreatedPayload(party.Id, party.Kind, party.DisplayName),
            $"party-created:{party.Id.Value}",
            party.TenantId,
            at,
            cancellationToken).ConfigureAwait(false);
        return party;
    }

    /// <summary>
    /// Creates a party and lets a caller enlist a second EF context in the same SQLite transaction.
    /// The callback runs after the party row is staged and before the transaction commits.
    /// </summary>
    public async Task<Party> CreateInTransactionAsync(
        TenantId tenantId,
        PartyKind kind,
        string displayName,
        PartyId actor,
        DateTimeOffset at,
        Func<LocalNodeDbContext, Party, IDbContextTransaction, CancellationToken, Task> enlist,
        PartyId? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enlist);

        var party = Party.Create(tenantId, kind, displayName, actor, new Instant(at), id);
        Throw(PartyValidator.Validate(party));

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await ctx.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await enlist(ctx, party, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await Publish(
            PeopleFoundationEventNames.PartyCreated,
            new PartyCreatedPayload(party.Id, party.Kind, party.DisplayName),
            $"party-created:{party.Id.Value}",
            party.TenantId,
            at,
            cancellationToken).ConfigureAwait(false);
        return party;
    }

    /// <inheritdoc />
    public async Task<Party> UpdateAsync(
        Party updated,
        PartyId actor,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == updated.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
            throw new InvalidOperationException($"Party '{updated.Id.Value}' does not exist.");
        if (existing.DeletedAt is not null)
            throw new InvalidOperationException($"Party '{updated.Id.Value}' is tombstoned; cannot update.");

        var stamped = updated with
        {
            UpdatedAt = new Instant(at),
            UpdatedBy = actor,
            Version = existing.Version + 1,
        };
        Throw(PartyValidator.Validate(stamped));

        ctx.Entry(existing).State = EntityState.Detached;
        ctx.Entry(stamped).State = EntityState.Modified;
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stamped;
    }

    /// <inheritdoc />
    public async Task<Party> DeleteAsync(
        PartyId id,
        string? reason,
        PartyId actor,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
            throw new InvalidOperationException($"Party '{id.Value}' does not exist.");
        if (existing.DeletedAt is not null)
            return existing; // idempotent

        var now = new Instant(at);
        var deleted = existing with
        {
            DeletedAt = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAt = now,
            UpdatedBy = actor,
            Version = existing.Version + 1,
        };

        ctx.Entry(existing).State = EntityState.Detached;
        ctx.Entry(deleted).State = EntityState.Modified;
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<PartyRole> AttachRoleAsync(
        PartyId partyId,
        string roleName,
        string roleRecordId,
        PartyId actor,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var party = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId, cancellationToken)
            .ConfigureAwait(false);
        if (party is null || party.DeletedAt is not null)
            throw new InvalidOperationException($"Party '{partyId.Value}' is not live; cannot attach role.");

        // Idempotency check
        var existing = await ctx.Set<PartyRole>()
            .AsNoTracking()
            .FirstOrDefaultAsync(pr => pr.PartyId == partyId
                && pr.RoleName == roleName
                && pr.RoleRecordId == roleRecordId
                && pr.EndedAt == null
                && pr.DeletedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var role = PartyRole.Create(
            party.TenantId, partyId, roleName, roleRecordId, actor,
            new Instant(at), startedAt: new Instant(at));
        Throw(PartyRoleValidator.Validate(role));
        ctx.Set<PartyRole>().Add(role);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role;
    }

    /// <inheritdoc />
    public async Task<PartyRole> DetachRoleAsync(
        PartyRoleId roleId,
        string? endedReason,
        PartyId actor,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var role = await ctx.Set<PartyRole>()
            .FirstOrDefaultAsync(pr => pr.Id == roleId, cancellationToken)
            .ConfigureAwait(false);
        if (role is null)
            throw new InvalidOperationException($"PartyRole '{roleId.Value}' does not exist.");
        if (role.EndedAt is not null)
            throw new InvalidOperationException($"PartyRole '{roleId.Value}' is already detached.");

        var ended = role.End(new Instant(at), endedReason, actor);
        Throw(PartyRoleValidator.Validate(ended));
        ctx.Entry(role).State = EntityState.Detached;
        ctx.Entry(ended).State = EntityState.Modified;
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ended;
    }

    /// <inheritdoc />
    public async Task<EmailAddress> AddEmailAsync(
        PartyId partyId,
        string address,
        bool isPrimary,
        PartyId actor,
        DateTimeOffset at,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var party = await RequireLiveParty(ctx, partyId, cancellationToken).ConfigureAwait(false);
        var email = EmailAddress.Create(
            party.TenantId, partyId, address, isPrimary, actor, new Instant(at), label);
        Throw(EmailAddressValidator.Validate(email));
        ctx.Set<EmailAddress>().Add(email);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return email;
    }

    /// <inheritdoc />
    public async Task<EmailAddress> SupersedeEmailAsync(
        EmailAddressId priorRowId,
        string address,
        bool isPrimary,
        PartyId actor,
        DateTimeOffset at,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var prior = await ctx.Set<EmailAddress>()
            .FirstOrDefaultAsync(ea => ea.Id == priorRowId, cancellationToken)
            .ConfigureAwait(false);
        if (prior is null)
            throw new InvalidOperationException($"EmailAddress '{priorRowId.Value}' does not exist.");
        if (prior.ReplacedAt is not null)
            throw new InvalidOperationException($"EmailAddress '{priorRowId.Value}' is already superseded.");
        if (prior.DeletedAt is not null)
            throw new InvalidOperationException($"EmailAddress '{priorRowId.Value}' is tombstoned.");

        var now = new Instant(at);
        var newRow = EmailAddress.Create(
            prior.TenantId, prior.PartyId, address, isPrimary, actor, now, label);
        Throw(EmailAddressValidator.Validate(newRow));

        var supersededPrior = prior with { ReplacedAt = now, UpdatedAt = now, UpdatedBy = actor, Version = prior.Version + 1 };
        ctx.Entry(prior).State = EntityState.Detached;
        ctx.Entry(supersededPrior).State = EntityState.Modified;
        ctx.Set<EmailAddress>().Add(newRow);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return newRow;
    }

    /// <inheritdoc />
    public async Task<PhoneNumber> AddPhoneAsync(
        PartyId partyId,
        string e164,
        bool isPrimary,
        PartyId actor,
        DateTimeOffset at,
        string? label = null,
        bool isMobile = false,
        string? extension = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var party = await RequireLiveParty(ctx, partyId, cancellationToken).ConfigureAwait(false);
        var phone = PhoneNumber.Create(
            party.TenantId, partyId, e164, isPrimary, actor, new Instant(at), label, extension, isMobile);
        Throw(PhoneNumberValidator.Validate(phone));
        ctx.Set<PhoneNumber>().Add(phone);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return phone;
    }

    /// <inheritdoc />
    public async Task<PartyAddress> AddAddressAsync(
        PartyId partyId,
        Address address,
        bool isPrimary,
        PartyId actor,
        DateTimeOffset at,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var party = await RequireLiveParty(ctx, partyId, cancellationToken).ConfigureAwait(false);
        var pa = PartyAddress.Create(
            party.TenantId, partyId, address, isPrimary, actor, new Instant(at), label);
        Throw(PartyAddressValidator.Validate(pa));
        ctx.Set<PartyAddress>().Add(pa);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pa;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internals
    // ──────────────────────────────────────────────────────────────────

    private static async Task<Party> RequireLiveParty(
        LocalNodeDbContext ctx,
        PartyId partyId,
        CancellationToken cancellationToken)
    {
        var party = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId, cancellationToken)
            .ConfigureAwait(false);
        if (party is null || party.DeletedAt is not null)
            throw new InvalidOperationException($"Party '{partyId.Value}' is not live; cannot perform operation.");
        return party;
    }

    private static void Throw(Harborline.Api.Blocks.People.Foundation.Validation.ValidationResult result)
    {
        if (!result.IsValid)
            throw new PartyValidationException(result);
    }

    private Task Publish<TPayload>(
        string eventType,
        TPayload payload,
        string idempotencyKey,
        TenantId tenantId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var envelope = new DomainEventEnvelope<TPayload>
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            SchemaVersion = 1,
            OccurredAt = at,
            TenantId = tenantId,
            OriginatingReplicaId = ReplicaId.System,
            IdempotencyKey = idempotencyKey,
            Payload = payload!,
        };
        return _events.PublishAsync(envelope, cancellationToken);
    }
}
