using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Durable, restart-safe <see cref="IInvoiceNumberingService"/> for the embedded local node
/// (Cohort D Step 2b). Mints customer-facing invoice numbers in the canonical
/// <c>INV-YYYY-MM-DD-{ReplicaId}-{seq:D4}</c> format the AR cluster requires for non-Draft invoices.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="InMemoryInvoiceNumberingService"/>?</b> The in-memory numbering service keeps
/// its per-(chart, replica) counter in a process-local dictionary; a node restart resets it to zero,
/// so the next mint would re-emit <c>…-0001</c> and collide with the existing first invoice on the EF
/// unique index <c>ux_invoices_tenant_chart_number</c> (ADR 0092). The single-device node needs a
/// monotonic sequence that survives restarts, so this implementation DERIVES the next sequence from
/// the MAX existing invoice number for the (chart, replica) in the recoverable <c>local-node.db</c> —
/// the same store the invoices live in. State is the store, not process memory.
/// </para>
/// <para>
/// <b>Single-writer monotonicity.</b> The local node serves exactly one operator, so there is no
/// cross-replica minting race on a single device; the sequence is computed as
/// <c>max(existing seq for (chart, this-replica)) + 1</c> under a process-local lock that serializes
/// concurrent mints within the process (defence against two in-flight create requests racing the
/// same MAX read). Gaps are allowed (a create whose mint succeeds but whose persist fails leaves a
/// gap — acceptable per the AR cluster's numbering contract).
/// </para>
/// <para>
/// <b>Replica suffix.</b> Pinned to the install's local replica id (default <c>"AA"</c>, mirroring
/// <see cref="BlocksFinancialArOptions.LocalReplicaId"/>). On a single-device node there is only one
/// replica, so the suffix is a constant; the per-replica filter on the MAX read keeps the sequence
/// independent of any imported numbers minted under a different replica suffix.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Uses a short-lived context from the injected factory (mirrors
/// <see cref="NodeEfInvoiceRepository"/> / <see cref="NodeEfJournalStore"/>).
/// </para>
/// <para>
/// <b>In-transaction home-failover fence on sequence allocation (security verdict G-4 / Gap-2b — the
/// TOCTOU the verdict named).</b> Number minting happens in this service's OWN short-lived context BEFORE
/// the JE write, so the JE-post fence alone would NOT stop a superseded ("stale") home from incrementing
/// the gap-free counter — exactly the Gap-2b window the security verdict flags. To close it, when an
/// ambient <see cref="HomeEpochWriteScope"/> is active this service runs the fence read + the MAX-number
/// scan inside an explicit <c>BEGIN IMMEDIATE</c> transaction (<see cref="HomeEpochFenceTransaction"/>): the
/// write/RESERVED lock is taken BEFORE the fence read, so the read sees the durably-current epoch and a
/// stale home throws <see cref="StaleHomeEpochException"/> BEFORE any number is computed — it can never
/// allocate a number (the counter does not advance for the rejected home). The numbering computation here is
/// read-only (no <c>SaveChanges</c>), so the IMMEDIATE lock's job at this site is to serialize the fence
/// read against a concurrent promotion's commit — without it the read would run in its own autocommit
/// statement and re-open the Gap-2b window (verdict earlier repository ticket #1365 Finding 1). A no-op (the original plain
/// read path, no explicit transaction) when no home-epoch scope is active (every single-device mint today).
/// </para>
/// </remarks>
public sealed class NodeEfInvoiceNumberingService : IInvoiceNumberingService, IDisposable
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly ReplicaId _localReplica;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Construct bound to the local-node EF context factory + the install's local replica id.</summary>
    public NodeEfInvoiceNumberingService(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        ReplicaId localReplica)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _localReplica = localReplica;
    }

    /// <inheritdoc />
    public async Task<string> NextNumberAsync(
        ChartOfAccountsId chartId,
        DateOnly issueDate,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prefix = $"INV-"; // all invoice numbers start INV-
            var replicaInfix = $"-{_localReplica.Value}-"; // …-{REPLICA}- segment

            await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            // Security verdict G-4 / Gap-2b (md2 Finding 1) — the fence read and the MAX-number scan must be
            // ONE atomic read-through under the write lock: otherwise a concurrent promotion could commit a
            // higher epoch AFTER the fence read but BEFORE the number is computed, and a stale home would
            // still mint (advancing the gap-free counter). When a multi-home write is in flight (an ambient
            // HomeEpochWriteScope), run the fence read + the candidate scan inside an explicit BEGIN
            // IMMEDIATE transaction (HomeEpochFenceTransaction): the RESERVED write lock is taken BEFORE the
            // fence read, so the read sees the durably-current epoch and a stale home throws
            // StaleHomeEpochException BEFORE any number is computed — it can never advance the counter. The
            // numbering computation itself is read-only, so there is no SaveChanges inside the transaction;
            // the IMMEDIATE lock's job here is to serialize the fence read against a concurrent promotion's
            // commit. A no-op (the original plain read path) when no scope is active — every single-device
            // mint today.
            string mintedNumber = null!;

            async Task ComputeNextNumberAsync(bool fence)
            {
                if (fence)
                {
                    await HomeEpochFence.AssertNotStaleAsync(ctx, HomeEpochWriteScope.Current, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Pull the candidate set server-side: live invoices in this chart whose number carries
                // this replica's suffix segment. The set is tiny on a single-device node; parse the
                // trailing sequence client-side (the trailing digits after the last '-') and take the max.
                // We can't translate the trailing-digit parse to SQL portably, so the WHERE narrows by
                // the structural prefix/infix and the small materialized set is scanned in memory.
                var candidates = await ctx.Set<Invoice>()
                    .AsNoTracking()
                    .Where(i => i.DeletedAtUtc == null
                             && i.ChartId == chartId
                             && i.InvoiceNumber.StartsWith(prefix)
                             && i.InvoiceNumber.Contains(replicaInfix))
                    .Select(i => i.InvoiceNumber)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                long maxSeq = 0L;
                foreach (var number in candidates)
                {
                    if (!InvoiceNumberFormat.IsWellFormed(number))
                    {
                        continue; // ignore any non-canonical number defensively
                    }
                    var lastDash = number.LastIndexOf('-');
                    if (lastDash < 0 || lastDash == number.Length - 1)
                    {
                        continue;
                    }
                    var seqPart = number[(lastDash + 1)..];
                    if (long.TryParse(seqPart, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                        && seq > maxSeq)
                    {
                        maxSeq = seq;
                    }
                }

                var next = maxSeq + 1L;
                // D4 padding minimum, expands beyond for very high-volume charts (10000+ invoices).
                var seqText = next < 10_000 ? next.ToString("D4", CultureInfo.InvariantCulture)
                                            : next.ToString(CultureInfo.InvariantCulture);
                mintedNumber = $"INV-{issueDate:yyyy-MM-dd}-{_localReplica.Value}-{seqText}";
            }

            if (HomeEpochWriteScope.Current is not null)
            {
                await HomeEpochFenceTransaction.RunAsync(
                    ctx,
                    () => ComputeNextNumberAsync(fence: true),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ComputeNextNumberAsync(fence: false).ConfigureAwait(false);
            }

            return mintedNumber;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<ReplicaId> ResolveCollisionAsync(
        ChartOfAccountsId chartId,
        string conflictingNumber,
        ReplicaId localReplica,
        ReplicaId remoteReplica,
        Instant localReplicaCreatedAt,
        Instant remoteReplicaCreatedAt,
        CancellationToken cancellationToken = default)
    {
        // Older replica wins → return the replica that MUST re-key. Identical arbitration rule as
        // InMemoryInvoiceNumberingService (single-device installs never hit this, but the contract
        // member is implemented for completeness + future multi-device sync).
        var localFirst = localReplicaCreatedAt.Value;
        var remoteFirst = remoteReplicaCreatedAt.Value;

        ReplicaId mustRekey;
        if (localFirst < remoteFirst)
        {
            mustRekey = remoteReplica;
        }
        else if (remoteFirst < localFirst)
        {
            mustRekey = localReplica;
        }
        else
        {
            // Equal timestamps → lexicographic on the ReplicaId value; larger string re-keys.
            mustRekey = string.CompareOrdinal(localReplica.Value, remoteReplica.Value) > 0
                ? localReplica
                : remoteReplica;
        }

        return Task.FromResult(mustRekey);
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
