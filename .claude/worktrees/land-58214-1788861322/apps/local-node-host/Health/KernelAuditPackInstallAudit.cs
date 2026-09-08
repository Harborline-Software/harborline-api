using System.Collections.Immutable;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Kernel.Audit;

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
/// </remarks>
public sealed class KernelAuditPackInstallAudit : IPackInstallAudit
{
    private readonly IAuthorizedAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly ILogger<KernelAuditPackInstallAudit> _logger;
    private readonly InMemoryPackInstallAudit _mirror = new();

    /// <summary>Constructs the adapter over the audit trail + the node principal signer.</summary>
    public KernelAuditPackInstallAudit(
        IAuthorizedAuditTrail trail,
        NodePrincipalSigner signer,
        ILogger<KernelAuditPackInstallAudit> logger)
    {
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        ArgumentNullException.ThrowIfNull(signer);
        _signer = signer.Signer;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        AppendCore(entry, decision);
    }

    private void AppendCore(PackInstallAuditEntry entry, AuthorizationDecision? decision)
    {
        try
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
                "Pack install audit append FAILED (tenant {Tenant}, {Action} {PackKey} v{Version}) — the "
                + "mutation stands but its durable audit envelope was not written.",
                entry.Tenant, entry.Action, entry.PackKey, entry.Version);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant) => _mirror.Query(tenant);
}
