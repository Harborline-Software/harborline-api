using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The host ADAPTER that binds the Pack Composer install engine's <see cref="IPackInstallAudit"/> port to
/// the unified, append-only, tamper-evident kernel audit trail (<see cref="IAuditTrail"/>) — the SAME
/// durable trail the financial posts + the SoD compensating controls write to. This is what CLOSES B-1a's
/// structured-log-only floor: every install / upgrade / activate / deactivate / break-glass / refusal becomes a
/// signed, durable audit ENVELOPE (design §6 / the B-1b obligation).
/// </summary>
/// <remarks>
/// Mirrors <see cref="KernelAuditEnrollmentCompensatingControlRecorder"/>: the adapter lives at the host (the only place that references
/// both the foundation-tier port and kernel-audit), signs each entry with the node principal signer, and is
/// FAIL-SAFE-BUT-LOUD — an <see cref="IAuditTrail.AppendAsync"/> fault must not brick an already-committed
/// install, but it is logged loudly (never fail-silent). A small in-memory MIRROR backs
/// <see cref="Query"/> for the list surface + tests; the durable envelope is the kernel trail.
/// <para>
/// T-1048 (DES-0029 ck-6): an install, activation or deactivation committed by the durable pack store does not append
/// after the commit. The store calls <see cref="Stage"/> inside its transaction, which stages the signed envelope as a
/// <see cref="NodeAuditOutbox"/> entry; the installer's later <see cref="AppendAuthorizedAsync"/> of that same entry
/// instance only mirrors it and kicks the outbox drain. A crash between the two loses nothing: the entry is owed in
/// <c>search_audit_outbox</c> and the drain delivers it once.
/// </para>
/// </remarks>
public sealed class KernelAuditPackInstallAudit : IPackInstallAudit
{
    private readonly IAuthorizedAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly ILogger<KernelAuditPackInstallAudit> _logger;
    private readonly NodeAuditOutbox? _outbox;
    private readonly InMemoryPackInstallAudit _mirror = new();

    // The entry instances a store commit staged. Keyed by identity and weakly, so an entry whose commit rolled back
    // (and is therefore never appended) is simply collected.
    private readonly ConditionalWeakTable<PackInstallAuditEntry, object> _staged = new();
    private readonly ConditionalWeakTable<PackInstallAuditEntry, PreparedAudit> _prepared = new();
    private sealed record PreparedAudit(AuditRecord Record, AuthorizationDecision Decision);

    /// <summary>Constructs the adapter over the audit trail + the node principal signer.</summary>
    /// <param name="trail">The trail an entry no store staged is appended to.</param>
    /// <param name="signer">Signs every envelope.</param>
    /// <param name="logger">Logs a failed append or delivery.</param>
    /// <param name="outbox">Drained after a staged entry's commit, so it reaches the trail now; null leaves it to the drain daemon.</param>
    public KernelAuditPackInstallAudit(
        IAuthorizedAuditTrail trail,
        NodePrincipalSigner signer,
        ILogger<KernelAuditPackInstallAudit> logger,
        NodeAuditOutbox? outbox = null)
        : this(trail, signer?.Signer ?? throw new ArgumentNullException(nameof(signer)), logger, outbox) { }

    internal KernelAuditPackInstallAudit(
        IAuthorizedAuditTrail trail, IOperationSigner signer,
        ILogger<KernelAuditPackInstallAudit> logger, NodeAuditOutbox? outbox = null)
    {
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _outbox = outbox;
    }

    /// <summary>
    /// T-1048: stages <paramref name="entry"/>'s signed envelope on <paramref name="write"/>, the context whose
    /// commit carries the pack change. Signing completed in preparation; a capture fault throws, so the change fails
    /// rather than committing unaudited.
    /// </summary>
    /// <returns>The staged id that the next entry in this ceremony names as its predecessor.</returns>
    internal Guid Stage(DbContext write, PackInstallAuditEntry entry, AuthorizationDecision decision, Guid? predecessorAuditId = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        // The entry's own header must be the act decided, as the direct authorized append checks; the staged row
        // then carries the decision's header, which this proves equal.
        CapturedAuditAuthority.Capture(entry.Tenant, entry.Actor, entry.OccurredAtUtc, entry.Target, entry.Act, decision);
        var prepared = RequirePrepared(entry, decision);
        NodeAuditOutbox.StageRecord(write, prepared.Record, decision);
        var id = prepared.Record.AuditId;
        write.Set<AuditOutboxRow>().Local.Single(row => row.AuditId == id.ToString("D"))
            .PredecessorAuditId = predecessorAuditId?.ToString("D");
        _staged.AddOrUpdate(entry, entry);
        return id;
    }

    /// <inheritdoc />
    public async ValueTask PrepareAuthorizedAsync(PackInstallAuditEntry entry, AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        if (entry.PreDecision)
            throw new ArgumentException("An authorized pack audit entry cannot be flagged preDecision.", nameof(entry));
        CapturedAuditAuthority.Capture(entry.Tenant, entry.Actor, entry.OccurredAtUtc, entry.Target, entry.Act, decision);
        ct.ThrowIfCancellationRequested();
        var payload = await _signer.SignAsync(new AuditPayload(Body(entry, decision)), entry.OccurredAtUtc,
            Guid.NewGuid(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var record = new AuditRecord(Guid.NewGuid(), entry.Tenant, PackInstallEventType, entry.OccurredAtUtc,
            payload, ImmutableArray<AttestingSignature>.Empty, Actor: entry.Actor, Target: entry.Target, Act: entry.Act);
        _prepared.AddOrUpdate(entry, new PreparedAudit(record, decision));
    }

    private PreparedAudit RequirePrepared(PackInstallAuditEntry entry, AuthorizationDecision decision)
    {
        if (!_prepared.TryGetValue(entry, out var prepared))
            throw new InvalidOperationException("Pack audit must be prepared before its synchronous commit.");
        if (!ReferenceEquals(prepared.Decision, decision))
            throw new InvalidOperationException("The prepared pack audit carries another authorization decision.");
        return prepared;
    }

    /// <summary>The pack-install audit event type on the unified trail.</summary>
    public static readonly AuditEventType PackInstallEventType = new("PackComposerInstall");

    /// <inheritdoc />
    public void Append(PackInstallAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.PreDecision)
            throw new ArgumentException("Ordinary pack audit entries must be flagged preDecision.", nameof(entry));
        _mirror.Append(entry);
        AppendCore(entry, decision: null);
    }

    /// <inheritdoc />
    public void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        if (entry.PreDecision)
            throw new ArgumentException("An authorized pack audit entry cannot be flagged preDecision.", nameof(entry));
        _mirror.AppendAuthorized(entry, decision);
        if (_staged.Remove(entry))
        {
            // A legacy synchronous caller leaves delivery to the existing durable drain daemon.
            _prepared.Remove(entry);
        }
        else
            AppendCore(entry, decision);
    }

    /// <inheritdoc />
    public async ValueTask AppendAuthorizedAsync(PackInstallAuditEntry entry, AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        if (entry.PreDecision)
            throw new ArgumentException("An authorized pack audit entry cannot be flagged preDecision.", nameof(entry));
        _mirror.AppendAuthorized(entry, decision);
        if (_staged.Remove(entry))
        {
            _prepared.Remove(entry);
            await DeliverAsync(ct).ConfigureAwait(false);
            return;
        }
        try
        {
            if (!_prepared.TryGetValue(entry, out _))
                await PrepareAuthorizedAsync(entry, decision, ct).ConfigureAwait(false);
            var prepared = RequirePrepared(entry, decision);
            await _trail.AppendAuthorizedAsync(prepared.Record, decision, ct).ConfigureAwait(false);
            _prepared.Remove(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Pack install audit append FAILED - the mutation stands but its durable audit envelope was not written.");
        }
    }

    private async ValueTask DeliverAsync(CancellationToken ct)
    {
        if (_outbox is null) return;
        try
        {
            await _outbox.DrainAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The entry is committed and owed; the drain daemon delivers it. The mutation stands.
            // Pack coordinates are caller-controlled; keep them in the signed audit, not diagnostic log text.
            _logger.LogError(ex, "Pack install audit delivery failed; the entry stays owed in the audit outbox.");
        }
    }

    private static Dictionary<string, object?> Body(PackInstallAuditEntry entry, AuthorizationDecision? decision)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = entry.Action.ToString(),
            ["packKey"] = entry.PackKey,
            ["version"] = entry.Version,
            ["signerKeyId"] = entry.SignerKeyId?.ToBase64Url(),
            ["epoch"] = entry.Epoch,
            ["detail"] = entry.Detail,
            ["breakGlassJustification"] = entry.BreakGlassJustification,
            ["breakGlassAuthorizingPrincipal"] = entry.BreakGlassAuthorizingPrincipal,
            ["preDecision"] = entry.PreDecision,
        };
        if (decision?.Request.CorrelationId is { } correlation)
            body["correlation_id"] = correlation.ToString("D");
        return body;
    }

    private void AppendCore(PackInstallAuditEntry entry, AuthorizationDecision? decision)
    {
        try
        {
            var body = Body(entry, decision);
            var occurredAt = entry.OccurredAtUtc;
            // The port is synchronous (the install engine is sync); block on the append. Installs are not a
            // hot path, and the mutation is already committed — this only records the durable envelope.
            var signed = _signer.SignAsync(new AuditPayload(body), occurredAt, Guid.NewGuid())
                .AsTask().GetAwaiter().GetResult();

            var record = new AuditRecord(
                AuditId: Guid.NewGuid(),
                TenantId: entry.Tenant,
                EventType: PackInstallEventType,
                OccurredAt: occurredAt,
                Payload: signed,
                AttestingSignatures: ImmutableArray<AttestingSignature>.Empty,
                Actor: entry.Actor,
                Target: entry.Target,
                Act: entry.Act);
            if (decision is null)
                _trail.AppendAsync(record).AsTask().GetAwaiter().GetResult();
            else
                _trail.AppendAuthorizedAsync(record, decision).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-safe-but-LOUD: the mutation already happened; a recording fault must not brick it, but a
            // pack install/upgrade/break-glass that could not be durably audited is a security-relevant gap.
            _logger.LogError(ex,
                "Pack install audit append FAILED - the mutation stands but its durable audit envelope was not written.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant) => _mirror.Query(tenant);
}
