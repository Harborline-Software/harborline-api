using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>
/// Contacts doctype adapter over the doctype-generic kernel CRDT projection.
/// </summary>
public sealed class ContactCrdtProjection : IDeltaProducer, IDeltaStateVectorProvider, IDeltaSink, IAsyncDisposable
{
    /// <summary>Logical CRDT document id for the contacts doctype.</summary>
    public const string DocumentId = "contacts";

    private readonly ContactCrdtSchema _schema;
    private readonly CrdtProjection<ContactCrdtSchema> _projection;
    private readonly ILogger<ContactCrdtProjection> _logger;

    /// <summary>Constructs the contacts adapter and its doctype schema.</summary>
    public ContactCrdtProjection(
        ICrdtEngine engine,
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        ILogger<ContactCrdtProjection> logger,
        ICrdtProjectionRegistry? projectionRegistry = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schema = new ContactCrdtSchema(
            contextFactory ?? throw new ArgumentNullException(nameof(contextFactory)), logger);
        _projection = new CrdtProjection<ContactCrdtSchema>(engine, _schema);
        projectionRegistry?.Register(_projection);
    }

    /// <summary>The CRDT document's current vector clock.</summary>
    public ReadOnlyMemory<byte> VectorClock => _projection.CurrentStateVector;

    /// <summary>Raised after a local contact mutation produces a CRDT operation.</summary>
    public event EventHandler? LocalDeltaProduced
    {
        add => _projection.LocalDeltaProduced += value;
        remove => _projection.LocalDeltaProduced -= value;
    }

    /// <summary>Seeds the contacts document from the durable read store.</summary>
    public Task<int> HydrateFromStoreAsync(CancellationToken ct) =>
        _schema.HydrateFromStoreAsync(_projection, ct);

    /// <summary>Number of contact keys currently present, including tombstones.</summary>
    public int Count => _projection.Read(static schema => schema.Count);

    /// <summary>Reads the converged state for one contact.</summary>
    public ContactCrdtState? GetState(string contactId) =>
        _projection.Read(schema => schema.GetState(contactId));

    /// <summary>Projects a persisted contact create or update.</summary>
    public void ProjectUpsert(Party party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _projection.Mutate(schema => schema.Set(party.Id.Value, ContactCrdtState.FromParty(party)));
    }

    /// <summary>Projects a persisted contact tombstone.</summary>
    public void ProjectDelete(Party tombstoned)
    {
        ArgumentNullException.ThrowIfNull(tombstoned);
        _projection.Mutate(schema => schema.Set(
            tombstoned.Id.Value,
            ContactCrdtState.FromParty(tombstoned) with { Deleted = true }));
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
        string documentId,
        ReadOnlyMemory<byte> peerVectorClock,
        CancellationToken ct)
    {
        ObserveDocumentId(documentId);
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_projection.EncodeDelta(peerVectorClock));
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> GetCurrentStateVectorAsync(
        string documentId,
        CancellationToken ct) =>
        ValueTask.FromResult(_projection.CurrentStateVector);

    /// <inheritdoc />
    public ValueTask ApplyInboundDeltaAsync(
        string documentId,
        ulong opSequence,
        ReadOnlyMemory<byte> delta,
        CancellationToken ct)
    {
        ObserveDocumentId(documentId);
        var result = _projection.ApplyDelta(documentId, opSequence, delta);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                result.Error,
                "Contacts CRDT bridge dropped inbound delta {DocumentId} op {OpSequence}: {Reason}",
                documentId,
                opSequence,
                result.Error?.Message);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Waits for all live change-driven reconciliations scheduled so far.</summary>
    internal Task DrainPendingReconcilesAsync() => _projection.DrainPendingReconcilesAsync();

    /// <summary>Reconciles one contact from the converged CRDT state.</summary>
    public Task ReconcileAsync(string contactId, CancellationToken ct) =>
        _projection.ReconcileAsync(CrdtProjectionChange.ForKey(contactId), ct);

    private void ObserveDocumentId(string documentId)
    {
        if (!string.Equals(documentId, DocumentId, StringComparison.Ordinal)
            && !string.Equals(documentId, "default", StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Contacts CRDT bridge received unexpected document id {DocumentId}; expected {Expected} or default.",
                documentId,
                DocumentId);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _projection.DisposeAsync();
}

internal sealed class ContactCrdtSchema : ICrdtProjectionSchema
{
    private const string MapName = "parties";

    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly ILogger<ContactCrdtProjection> _logger;
    private ICrdtMap? _parties;
    private EventHandler<CrdtMapChangedEventArgs>? _changedHandler;

    public ContactCrdtSchema(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        ILogger<ContactCrdtProjection> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public string DocumentId => ContactCrdtProjection.DocumentId;

    public int Count => Parties.Count;

    public void Bind(ICrdtDocument document, Action<CrdtProjectionChange> changed)
    {
        _parties = document.GetMap(MapName);
        _changedHandler = (_, args) => changed(CrdtProjectionChange.ForKey(args.Key));
        _parties.Changed += _changedHandler;
    }

    public void Unbind()
    {
        if (_parties is not null && _changedHandler is not null)
        {
            _parties.Changed -= _changedHandler;
        }
    }

    public ContactCrdtState? GetState(string contactId) => Parties.Get<ContactCrdtState>(contactId);

    public void Set(string contactId, ContactCrdtState state) => Parties.Set(contactId, state);

    public async Task<int> HydrateFromStoreAsync(
        CrdtProjection<ContactCrdtSchema> projection,
        CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.Set<Party>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var party in rows)
        {
            var state = ContactCrdtState.FromParty(party);
            if (party.DeletedAt is not null)
            {
                state = state with { Deleted = true };
            }

            projection.Mutate(schema => schema.Set(party.Id.Value, state));
        }

        _logger.LogInformation(
            "Contacts CRDT cold-start hydration projected {Count} contact(s) from local-node.db into the sync document.",
            rows.Count);
        return rows.Count;
    }

    public async ValueTask ReconcileAsync(CrdtProjectionChange change, CancellationToken ct)
    {
        if (change.Key is null)
        {
            foreach (var key in Parties.Keys)
            {
                await ReconcileContactAsync(key, ct).ConfigureAwait(false);
            }
            return;
        }

        await ReconcileContactAsync(change.Key, ct).ConfigureAwait(false);
    }

    private async Task ReconcileContactAsync(string contactId, CancellationToken ct)
    {
        var merged = Parties.Get<ContactCrdtState>(contactId);
        if (merged is null)
        {
            return;
        }

        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await ctx.Set<Party>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == new PartyId(contactId), ct)
                .ConfigureAwait(false);

            if (existing is not null && ContactCrdtState.FromParty(existing) == merged)
            {
                return;
            }

            var mergedParty = merged.ToParty();
            Party row = existing is null
                ? mergedParty
                : existing with
                {
                    Kind = mergedParty.Kind,
                    DisplayName = merged.DisplayName,
                    LegalName = merged.LegalName,
                    Notes = merged.Notes,
                    DoNotContact = merged.DoNotContact,
                    DoNotEmail = merged.DoNotEmail,
                    DoNotCall = merged.DoNotCall,
                    DoNotSms = merged.DoNotSms,
                    DeletedAt = merged.Deleted ? mergedParty.DeletedAt : null,
                    DeletedBy = merged.Deleted ? mergedParty.DeletedBy : null,
                    UpdatedAt = mergedParty.UpdatedAt,
                    UpdatedBy = merged.UpdatedBy is null ? (PartyId?)null : new PartyId(merged.UpdatedBy),
                    Version = merged.Version,
                };

            if (existing is null)
            {
                ctx.Set<Party>().Add(row);
            }
            else
            {
                ctx.Entry(existing).State = EntityState.Detached;
                ctx.Entry(row).State = EntityState.Modified;
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogDebug(
                "Contacts CRDT reconcile applied {ContactId} v{Version} to EF store",
                contactId,
                merged.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Contacts CRDT reconcile failed for {ContactId}: {Reason}",
                contactId,
                ex.Message);
        }
    }

    private ICrdtMap Parties =>
        _parties ?? throw new InvalidOperationException("The contacts schema is not bound to a CRDT document.");
}
