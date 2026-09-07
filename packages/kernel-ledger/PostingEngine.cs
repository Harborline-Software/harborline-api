using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Events;
using Harborline.Api.Kernel.Ledger.Exceptions;
using Harborline.Api.Kernel.Ledger.Periods;

using LeaseNs = Harborline.Api.Kernel.Lease;

namespace Harborline.Api.Kernel.Ledger;

/// <summary>
/// Default <see cref="IPostingEngine"/> implementation. Paper §12.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The engine keeps an in-memory index mapping
/// <see cref="Transaction.IdempotencyKey"/> → committed <see cref="Transaction.TransactionId"/>.
/// On construction the index is rebuilt by replaying the durable <see cref="IEventLog"/>
/// (ADR 0115 §D4 crash-durability gate). Calls to <see cref="PostAsync"/> that hit an existing
/// key short-circuit and return the prior transaction id without appending a new event.
/// </para>
/// <para>
/// <b>Crash durability (ADR 0115 §D4).</b> The engine's projection-replay state, idempotency
/// dedupe maps, and observed period-close boundary are <i>not</i> kept solely in a transient
/// in-memory list — on construction they are rebuilt <b>atomically</b> from the durable
/// <see cref="IEventLog"/> before the engine accepts any write. Without this, a node restart left
/// the dedupe maps empty while the on-disk log held prior events, so a client retrying a posting
/// with the same idempotency key after a restart would bypass dedupe and commit a <i>duplicate</i>
/// financial posting (double-counted money). The durable log is the source of truth; the
/// in-memory <see cref="_events"/> list is a rehydrated cache of it. Every event that mutates
/// rebuildable state — postings-applied, compensation-applied, period-closed — is appended to the
/// durable log so a restart reconstructs identical state. Torn-tail safety is provided by
/// <see cref="FileBackedEventLog"/>'s recovery-on-open: a partially-written trailing CBOR record is
/// truncated to the last good boundary, so replay sees only complete records.
/// </para>
/// <para>
/// <b>Lease scope.</b> A single <see cref="Transaction"/> may touch multiple
/// accounts. The engine acquires one lease <i>per affected account</i>
/// (resource id <c>ledger:account:{accountId}</c>), holds them for the
/// duration of the append, then releases them. Per-account (rather than
/// per-transaction-global) scope lets concurrent transactions that touch
/// disjoint accounts proceed in parallel — matching paper §6.3's CP-class
/// serialization requirement without over-serializing. Account ids are sorted
/// deterministically before acquisition to avoid lease-ordering deadlocks
/// between two transactions that touch the same pair of accounts.
/// </para>
/// <para>
/// <b>Closed periods.</b> When the injected <see cref="IPeriodCloseState"/>
/// indicates a period is closed, postings whose <see cref="Posting.PostedAt"/>
/// falls at or before <c>LastClosedPeriodEnd</c> are rewritten to an
/// <c>adjustments-yyyyMMdd</c> account in the next open period before the
/// transaction is committed (paper §12.4).
/// </para>
/// </remarks>
public sealed class PostingEngine : IPostingEngine, ILedgerEventStream
{
    /// <summary>Default lease duration used for account serialization.</summary>
    internal static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    // Kernel-event Kind discriminators for the three durable ledger event shapes.
    internal const string KindPostingsApplied = "ledger.postings-applied";
    internal const string KindCompensationApplied = "ledger.compensation-applied";
    internal const string KindPeriodClosed = "ledger.period-closed";

    private readonly LeaseNs.ILeaseCoordinator _leases;
    private readonly IEventLog _eventLog;
    private readonly IPeriodCloseState _periodState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostingEngine> _logger;

    // TransactionId → PostingsAppliedEvent (used by CompensateAsync for lookups).
    private readonly ConcurrentDictionary<Guid, PostingsAppliedEvent> _byTransactionId = new();

    // IdempotencyKey → committed TransactionId (paper §12.2 dedupe).
    private readonly ConcurrentDictionary<string, Guid> _byIdempotencyKey = new();

    // Append-ordered list of committed events for projection replay. This is a rehydrated cache of
    // the durable IEventLog (see crash-durability remarks) — NOT the source of truth.
    private readonly List<object> _events = new();
    private readonly object _eventsGate = new();

    // Live subscribers for projections.
    private readonly List<Action<object>> _subscribers = new();
    private readonly object _subscribersGate = new();

    /// <summary>Constructs a new posting engine, rehydrating durable state from the event log.</summary>
    public PostingEngine(
        LeaseNs.ILeaseCoordinator leases,
        IEventLog eventLog,
        IPeriodCloseState periodState,
        TimeProvider timeProvider,
        ILogger<PostingEngine>? logger = null)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _periodState = periodState ?? throw new ArgumentNullException(nameof(periodState));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<PostingEngine>.Instance;

        // ADR 0115 §D4 — rebuild projection-replay state + idempotency dedupe maps + observed
        // period-close boundary from the durable log BEFORE the engine accepts any write. Synchronous
        // and in the ctor so there is no window in which a write (and therefore a retried idempotency
        // key) could be processed against empty dedupe maps. The log read is a bounded, ct-free local
        // replay; FileBackedEventLog has already truncated any torn tail on its own open.
        RehydrateFromDurableLog();
    }

    /// <inheritdoc />
    public async Task<PostingResult> PostAsync(Transaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ct.ThrowIfCancellationRequested();

        // Balance validation — paper §12.1.
        if (!tx.IsBalanced)
        {
            _logger.LogWarning("Rejecting unbalanced transaction {TxId} (sum={Sum})", tx.TransactionId, tx.Sum);
            return new PostingResult(false, tx.TransactionId, "UNBALANCED", null);
        }

        // Idempotency dedupe — paper §12.2.
        if (_byIdempotencyKey.TryGetValue(tx.IdempotencyKey, out var existingId))
        {
            _logger.LogDebug(
                "Idempotency key {Key} already committed as {ExistingId}; returning prior result",
                tx.IdempotencyKey, existingId);
            return new PostingResult(true, existingId, null, null);
        }

        // Closed-period rewrite — paper §12.4.
        var effectiveTx = RewriteForClosedPeriod(tx);

        // Acquire per-account leases in a deterministic order.
        var accountIds = effectiveTx.Postings
            .Select(p => p.AccountId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();

        var heldLeases = new List<LeaseNs.Lease>(accountIds.Length);
        try
        {
            foreach (var accountId in accountIds)
            {
                var lease = await _leases.AcquireAsync(
                    $"ledger:account:{accountId}",
                    DefaultLeaseDuration,
                    ct).ConfigureAwait(false);
                if (lease is null)
                {
                    _logger.LogWarning(
                        "Lease unavailable for account {AccountId} while posting {TxId}",
                        accountId, effectiveTx.TransactionId);
                    return new PostingResult(false, effectiveTx.TransactionId, "QUORUM_UNAVAILABLE", null);
                }
                heldLeases.Add(lease);
            }

            // Append to kernel event log (durability receipt — the source of truth) FIRST, then
            // commit to the in-memory replay cache. The durable envelope carries full posting detail
            // so a restart can rebuild balances + dedupe maps, not just dedupe (ADR 0115 §D4).
            var envelope = BuildPostingsAppliedEnvelope(effectiveTx);
            var logSeq = await _eventLog.AppendAsync(envelope, ct).ConfigureAwait(false);

            var applied = new PostingsAppliedEvent(effectiveTx);
            CommitEvent(applied);
            _byTransactionId[effectiveTx.TransactionId] = applied;
            _byIdempotencyKey[effectiveTx.IdempotencyKey] = effectiveTx.TransactionId;

            return new PostingResult(true, effectiveTx.TransactionId, null, logSeq);
        }
        finally
        {
            foreach (var lease in heldLeases)
            {
                try
                {
                    await _leases.ReleaseAsync(lease, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Swallowed error releasing lease {LeaseId}", lease.LeaseId);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<PostingResult> CompensateAsync(Guid originalTransactionId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ct.ThrowIfCancellationRequested();

        if (!_byTransactionId.TryGetValue(originalTransactionId, out var original))
        {
            _logger.LogWarning("Compensate failed: transaction {TxId} not found", originalTransactionId);
            throw new InvalidOperationException(
                $"Cannot compensate transaction {originalTransactionId}: not found in ledger.");
        }

        var at = _timeProvider.GetUtcNow();
        var compensatingTxId = Guid.NewGuid();
        var compensatingPostings = original.Transaction.Postings.Select(p => new Posting(
            PostingId: Guid.NewGuid(),
            TransactionId: compensatingTxId,
            AccountId: p.AccountId,
            Amount: -p.Amount,
            Currency: p.Currency,
            PostedAt: at,
            Description: $"Compensation of {p.Description}: {reason}",
            Metadata: p.Metadata)).ToList();

        var compensating = new Transaction(
            TransactionId: compensatingTxId,
            IdempotencyKey: $"compensation:{originalTransactionId}:{reason}",
            Postings: compensatingPostings,
            CreatedAt: at);

        // PostAsync handles lease acquisition, balance check, idempotency, and the durable append of
        // the compensating PostingsAppliedEvent (which is what actually reverses the balances).
        var result = await PostAsync(compensating, ct).ConfigureAwait(false);
        if (result.Success && result.LogSequence is not null)
        {
            // Durably persist the compensation-applied marker too, so ReplayAll() after a restart
            // reconstructs identical event ordering. Skip when PostAsync deduped (LogSequence null) —
            // the marker is already on the durable log from the first compensation.
            var compensationEvent = new CompensationAppliedEvent(originalTransactionId, compensating, reason);
            var marker = BuildCompensationAppliedEnvelope(compensationEvent);
            await _eventLog.AppendAsync(marker, ct).ConfigureAwait(false);
            CommitEvent(compensationEvent);
        }
        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<object> ReplayAll()
    {
        lock (_eventsGate)
        {
            return _events.ToArray();
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<object> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subscribersGate)
        {
            _subscribers.Add(handler);
        }
        return new Unsubscriber(this, handler);
    }

    /// <summary>
    /// Internal hook for <see cref="IPeriodCloser"/> to durably persist and publish a
    /// <see cref="PeriodClosedEvent"/> through the same stream subscribers see. The durable append
    /// makes the closed-period boundary survive a restart (ADR 0115 §D4) so the rebuilt
    /// <see cref="IPeriodCloseState"/> still refuses to re-close a closed period.
    /// </summary>
    internal async Task PublishPeriodClosedAsync(PeriodClosedEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var envelope = BuildPeriodClosedEnvelope(evt);
        await _eventLog.AppendAsync(envelope, ct).ConfigureAwait(false);
        CommitEvent(evt);
    }

    private void CommitEvent(object evt)
    {
        lock (_eventsGate)
        {
            _events.Add(evt);
        }

        Action<object>[] snapshot;
        lock (_subscribersGate)
        {
            snapshot = _subscribers.ToArray();
        }
        foreach (var s in snapshot)
        {
            try
            {
                s(evt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Subscriber threw while handling ledger event");
            }
        }
    }

    private Transaction RewriteForClosedPeriod(Transaction tx)
    {
        var lastClosed = _periodState.LastClosedPeriodEnd;
        if (lastClosed is null)
        {
            return tx;
        }

        var closedEnd = lastClosed.Value;
        if (tx.Postings.All(p => p.PostedAt > closedEnd))
        {
            return tx;
        }

        var adjustmentSuffix = closedEnd.UtcDateTime.ToString("yyyyMMdd");
        var rewritten = tx.Postings.Select(p => p.PostedAt <= closedEnd
            ? p with
            {
                AccountId = $"adjustments-{adjustmentSuffix}",
                PostedAt = closedEnd.AddTicks(1),
                Description = $"[adj {adjustmentSuffix}] {p.Description}",
            }
            : p).ToList();

        return tx with { Postings = rewritten };
    }

    // ---- durable-log rehydration (ADR 0115 §D4) ----

    /// <summary>
    /// Replays the durable <see cref="IEventLog"/> on construction to rebuild the in-memory replay
    /// cache, the idempotency dedupe maps, the transaction-id index, and the observed period-close
    /// boundary. The durable log is the source of truth; this reconstructs the engine's view of it.
    /// Runs synchronously so no write can race a partially-rehydrated engine.
    /// </summary>
    private void RehydrateFromDurableLog()
    {
        var entries = ReadAllDurableEntries();

        DateTimeOffset? rebuiltLastClosed = null;
        var recovered = 0;

        foreach (var entry in entries)
        {
            var evt = TryReconstructEvent(entry.Event);
            if (evt is null)
            {
                continue;
            }

            switch (evt)
            {
                case PostingsAppliedEvent applied:
                    _events.Add(applied);
                    _byTransactionId[applied.Transaction.TransactionId] = applied;
                    _byIdempotencyKey[applied.Transaction.IdempotencyKey] = applied.Transaction.TransactionId;
                    recovered++;
                    break;

                case CompensationAppliedEvent comp:
                    _events.Add(comp);
                    recovered++;
                    break;

                case PeriodClosedEvent closed:
                    _events.Add(closed);
                    if (rebuiltLastClosed is null || closed.PeriodEnd > rebuiltLastClosed.Value)
                    {
                        rebuiltLastClosed = closed.PeriodEnd;
                    }
                    recovered++;
                    break;
            }
        }

        if (rebuiltLastClosed is not null && _periodState is PeriodCloseState concrete)
        {
            concrete.SetLastClosed(rebuiltLastClosed.Value);
        }

        if (recovered > 0)
        {
            _logger.LogInformation(
                "Rehydrated ledger from durable log: {EventCount} events, {TxCount} transactions, last-closed-period={LastClosed}.",
                recovered, _byTransactionId.Count, rebuiltLastClosed);
        }
    }

    private List<LogEntry> ReadAllDurableEntries()
    {
        var collected = new List<LogEntry>();
        // Synchronously drain the async enumerator. ReadAfterAsync(0) yields every durable record in
        // append order; FileBackedEventLog has already truncated any torn tail on its own open, so we
        // only ever see complete CBOR frames here.
        var enumerator = _eventLog.ReadAfterAsync(0, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();
                var hasNext = moveNext.IsCompleted
                    ? moveNext.Result
                    : moveNext.AsTask().GetAwaiter().GetResult();
                if (!hasNext)
                {
                    break;
                }
                collected.Add(enumerator.Current);
            }
        }
        finally
        {
            var dispose = enumerator.DisposeAsync();
            if (!dispose.IsCompleted)
            {
                dispose.AsTask().GetAwaiter().GetResult();
            }
        }
        return collected;
    }

    /// <summary>
    /// Reconstruct a typed ledger event from a durable <see cref="KernelEvent"/> envelope. Returns
    /// null for envelopes that don't carry enough detail to rebuild a typed event (and that cannot
    /// even prime the dedupe map). A thin legacy postings-applied envelope (no <c>postings</c> array)
    /// is reconstructed as an empty-posting <see cref="PostingsAppliedEvent"/> that seats the dedupe
    /// entry without adding balance motion.
    /// </summary>
    private object? TryReconstructEvent(KernelEvent evt)
    {
        try
        {
            return evt.Kind switch
            {
                KindPostingsApplied => ReconstructPostingsApplied(evt.Payload) ?? PrimeDedupeFallback(evt.Payload),
                KindCompensationApplied => ReconstructCompensationApplied(evt.Payload),
                KindPeriodClosed => ReconstructPeriodClosed(evt.Payload),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping un-reconstructable ledger envelope of kind {Kind}.", evt.Kind);
            return null;
        }
    }

    /// <summary>
    /// A thin legacy postings-applied envelope (no <c>detail</c> JSON field) cannot rebuild balances,
    /// but MUST still prime the idempotency dedupe map so a retried key after restart is caught. We
    /// synthesize a minimal transaction carrying only the original tx-id + key; it adds no balance
    /// motion (empty postings ⇒ projections ignore it) but seats the dedupe entry.
    /// </summary>
    private static object? PrimeDedupeFallback(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryGetGuid(payload, KeyTransactionId, out var txId) ||
            !TryGetString(payload, KeyIdempotencyKey, out var key))
        {
            return null;
        }
        var tx = new Transaction(txId, key, Array.Empty<Posting>(), DateTimeOffset.UnixEpoch);
        return new PostingsAppliedEvent(tx);
    }

    // ---- durable envelope codec (full-detail, ADR 0115 §D4) ----
    //
    // The kernel IEventLog CBOR codec round-trips only flat scalar payload values (string, bool,
    // int/long/ulong, float/double, byte[], DateTimeOffset) — nested arrays/maps are stringified and
    // not recoverable. So the structured ledger detail (the postings list, the closing-balances map)
    // is encoded as a single self-contained JSON string payload field ("detail"), keeping the kernel
    // codec contract untouched while making the durable log genuinely sufficient to rebuild the full
    // projection. Decimals are JSON numbers via the DTO (System.Text.Json round-trips System.Decimal
    // exactly); DateTimeOffset uses round-trip "O" formatting.

    private const string KeyTransactionId = "transactionId";
    private const string KeyIdempotencyKey = "idempotencyKey";
    private const string KeyDetail = "detail";

    private static readonly JsonSerializerOptions DetailJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static KernelEvent BuildPostingsAppliedEnvelope(Transaction tx)
    {
        var detail = new PostingsAppliedDetail(
            TransactionId: tx.TransactionId,
            IdempotencyKey: tx.IdempotencyKey,
            CreatedAt: tx.CreatedAt,
            Postings: tx.Postings.Select(PostingDto.From).ToList());

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Flat dedupe-bearing fields kept native so even a partial/forward-incompatible reader can
            // still seat the dedupe entry without parsing JSON.
            [KeyTransactionId] = tx.TransactionId.ToString("D"),
            [KeyIdempotencyKey] = tx.IdempotencyKey,
            [KeyDetail] = JsonSerializer.Serialize(detail, DetailJson),
        };

        return new KernelEvent(
            Id: new Harborline.Api.Kernel.Events.EventId(tx.TransactionId),
            EntityId: new EntityId("ledger", "txn", tx.TransactionId.ToString("N")),
            Kind: KindPostingsApplied,
            OccurredAt: tx.CreatedAt,
            Payload: payload);
    }

    private static KernelEvent BuildCompensationAppliedEnvelope(CompensationAppliedEvent evt)
    {
        var detail = new CompensationAppliedDetail(
            OriginalTransactionId: evt.OriginalTransactionId,
            Reason: evt.Reason,
            CompensatingTransactionId: evt.CompensatingTx.TransactionId,
            CompensatingIdempotencyKey: evt.CompensatingTx.IdempotencyKey,
            CompensatingCreatedAt: evt.CompensatingTx.CreatedAt,
            CompensatingPostings: evt.CompensatingTx.Postings.Select(PostingDto.From).ToList());

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [KeyDetail] = JsonSerializer.Serialize(detail, DetailJson),
        };

        return new KernelEvent(
            Id: new Harborline.Api.Kernel.Events.EventId(evt.CompensatingTx.TransactionId),
            EntityId: new EntityId("ledger", "txn", evt.OriginalTransactionId.ToString("N")),
            Kind: KindCompensationApplied,
            OccurredAt: evt.CompensatingTx.CreatedAt,
            Payload: payload);
    }

    private static KernelEvent BuildPeriodClosedEnvelope(PeriodClosedEvent evt)
    {
        var detail = new PeriodClosedDetail(
            PeriodEnd: evt.PeriodEnd,
            ClosingBalances: new Dictionary<string, decimal>(evt.ClosingBalances, StringComparer.Ordinal));

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [KeyDetail] = JsonSerializer.Serialize(detail, DetailJson),
        };

        return new KernelEvent(
            Id: Harborline.Api.Kernel.Events.EventId.NewId(),
            EntityId: new EntityId("ledger", "period", evt.PeriodEnd.UtcDateTime.ToString("yyyyMMddHHmmss")),
            Kind: KindPeriodClosed,
            OccurredAt: evt.PeriodEnd,
            Payload: payload);
    }

    private static PostingsAppliedEvent? ReconstructPostingsApplied(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryGetString(payload, KeyDetail, out var json))
        {
            return null; // thin legacy envelope — caller falls back to dedupe-only priming.
        }
        var detail = JsonSerializer.Deserialize<PostingsAppliedDetail>(json, DetailJson);
        if (detail is null)
        {
            return null;
        }
        var tx = new Transaction(
            detail.TransactionId,
            detail.IdempotencyKey,
            detail.Postings.Select(p => p.ToPosting()).ToList(),
            detail.CreatedAt);
        return new PostingsAppliedEvent(tx);
    }

    private static CompensationAppliedEvent? ReconstructCompensationApplied(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryGetString(payload, KeyDetail, out var json))
        {
            return null;
        }
        var detail = JsonSerializer.Deserialize<CompensationAppliedDetail>(json, DetailJson);
        if (detail is null)
        {
            return null;
        }
        var compensatingTx = new Transaction(
            detail.CompensatingTransactionId,
            detail.CompensatingIdempotencyKey,
            detail.CompensatingPostings.Select(p => p.ToPosting()).ToList(),
            detail.CompensatingCreatedAt);
        return new CompensationAppliedEvent(detail.OriginalTransactionId, compensatingTx, detail.Reason);
    }

    private static PeriodClosedEvent? ReconstructPeriodClosed(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryGetString(payload, KeyDetail, out var json))
        {
            return null;
        }
        var detail = JsonSerializer.Deserialize<PeriodClosedDetail>(json, DetailJson);
        if (detail is null)
        {
            return null;
        }
        return new PeriodClosedEvent(
            detail.PeriodEnd,
            new Dictionary<string, decimal>(detail.ClosingBalances, StringComparer.Ordinal));
    }

    private static bool TryGetString(IReadOnlyDictionary<string, object?> map, string key, out string value)
    {
        if (map.TryGetValue(key, out var raw) && raw is string s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetGuid(IReadOnlyDictionary<string, object?> map, string key, out Guid value)
    {
        if (map.TryGetValue(key, out var raw) && raw is string s && Guid.TryParse(s, out value))
        {
            return true;
        }
        value = Guid.Empty;
        return false;
    }

    // ---- durable detail DTOs (JSON-encoded inside the "detail" payload field) ----

    private sealed record PostingsAppliedDetail(
        Guid TransactionId,
        string IdempotencyKey,
        DateTimeOffset CreatedAt,
        List<PostingDto> Postings);

    private sealed record CompensationAppliedDetail(
        Guid OriginalTransactionId,
        string Reason,
        Guid CompensatingTransactionId,
        string CompensatingIdempotencyKey,
        DateTimeOffset CompensatingCreatedAt,
        List<PostingDto> CompensatingPostings);

    private sealed record PeriodClosedDetail(
        DateTimeOffset PeriodEnd,
        Dictionary<string, decimal> ClosingBalances);

    private sealed record PostingDto(
        Guid PostingId,
        Guid TransactionId,
        string AccountId,
        decimal Amount,
        string Currency,
        DateTimeOffset PostedAt,
        string Description,
        Dictionary<string, string> Metadata)
    {
        public static PostingDto From(Posting p) => new(
            p.PostingId,
            p.TransactionId,
            p.AccountId,
            p.Amount,
            p.Currency,
            p.PostedAt,
            p.Description,
            new Dictionary<string, string>(p.Metadata, StringComparer.Ordinal));

        public Posting ToPosting() => new(
            PostingId,
            TransactionId,
            AccountId,
            Amount,
            Currency,
            PostedAt,
            Description,
            Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly PostingEngine _engine;
        private readonly Action<object> _handler;
        private int _disposed;

        public Unsubscriber(PostingEngine engine, Action<object> handler)
        {
            _engine = engine;
            _handler = handler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (_engine._subscribersGate)
            {
                _engine._subscribers.Remove(_handler);
            }
        }
    }
}
