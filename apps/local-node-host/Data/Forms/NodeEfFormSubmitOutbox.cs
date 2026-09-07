using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Durable <see cref="IFormSubmitOutbox"/> stored in the node's SQLCipher-backed
/// <c>local-node.db</c>. Rows retain their explicit tenant id and are recovered in append order.
/// </summary>
public sealed class NodeEfFormSubmitOutbox : IFormSubmitOutbox
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Creates the outbox over the local-node relational context factory.</summary>
    public NodeEfFormSubmitOutbox(IDbContextFactory<LocalNodeDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task<FormSubmitOutboxEntry> EnqueueAsync(
        FormSubmitContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidate = FormSubmitOutboxEntry.FromContext(context);

        await using (var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var existing = await FindByEntryIdAsync(db, candidate.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return ExistingForTenant(existing, candidate);
            }

            db.Set<FormSubmitOutboxRow>().Add(ToRow(candidate));
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (DbUpdateException)
            {
                // A concurrent replay may have won the unique EntryId insert. Re-open a clean context
                // and return that durable row if so; otherwise preserve the original persistence fault.
            }
        }

        await using var retry = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var raced = await FindByEntryIdAsync(retry, candidate.Id, cancellationToken).ConfigureAwait(false);
        if (raced is null)
        {
            throw new InvalidOperationException(
                $"Form-submit outbox entry '{candidate.Id}' failed to persist and no concurrent row exists.");
        }

        return ExistingForTenant(raced, candidate);
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);

        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var updated = await db.Set<FormSubmitOutboxRow>()
            .Where(row => row.EntryId == entryId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.State, FormSubmitOutboxState.Completed)
                    .SetProperty(row => row.LastError, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfMissing(updated, entryId);
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(
        string entryId,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        ArgumentNullException.ThrowIfNull(error);

        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var updated = await db.Set<FormSubmitOutboxRow>()
            .Where(row => row.EntryId == entryId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.State, FormSubmitOutboxState.Failed)
                    .SetProperty(row => row.Attempts, row => row.Attempts + 1)
                    .SetProperty(row => row.LastError, error),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfMissing(updated, entryId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormSubmitOutboxEntry>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // This is deliberately a system recovery sweep across every tenant. Each row retains its tenant,
        // and RebuildContext carries that tenant back into the projection authorization boundary.
        return await db.Set<FormSubmitOutboxRow>()
            .AsNoTracking()
            .Where(row => row.State != FormSubmitOutboxState.Completed)
            .OrderBy(row => row.Sequence)
            .Select(row => ToEntry(row))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FormSubmitOutboxEntry?> GetAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);

        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await FindByEntryIdAsync(db, entryId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    private static async Task<FormSubmitOutboxRow?> FindByEntryIdAsync(
        LocalNodeDbContext db,
        string entryId,
        CancellationToken cancellationToken) =>
        await db.Set<FormSubmitOutboxRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.EntryId == entryId, cancellationToken)
            .ConfigureAwait(false);

    private static FormSubmitOutboxEntry ExistingForTenant(
        FormSubmitOutboxRow row,
        FormSubmitOutboxEntry candidate)
    {
        if (!string.Equals(row.TenantId, candidate.Tenant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Form-submit outbox entry '{candidate.Id}' already belongs to a different tenant.");
        }

        return ToEntry(row);
    }

    private static FormSubmitOutboxRow ToRow(FormSubmitOutboxEntry entry) => new()
    {
        EntryId = entry.Id,
        FormId = entry.Form,
        InstanceId = entry.InstanceId,
        TenantId = entry.Tenant,
        ActorId = entry.Actor,
        SubmittedAt = entry.SubmittedAt,
        SubmittedValuesJson = entry.SubmittedValuesJson,
        CaseRef = entry.CaseRef,
        State = entry.State,
        Attempts = entry.Attempts,
        LastError = entry.LastError,
    };

    private static FormSubmitOutboxEntry ToEntry(FormSubmitOutboxRow row) => new(
        Id: row.EntryId,
        Form: row.FormId,
        InstanceId: row.InstanceId,
        Tenant: row.TenantId,
        Actor: row.ActorId,
        SubmittedAt: row.SubmittedAt,
        SubmittedValuesJson: row.SubmittedValuesJson,
        CaseRef: row.CaseRef,
        State: row.State,
        Attempts: row.Attempts,
        LastError: row.LastError);

    private static void ThrowIfMissing(int updated, string entryId)
    {
        if (updated == 0)
        {
            throw new InvalidOperationException($"Outbox entry '{entryId}' does not exist.");
        }
    }
}
