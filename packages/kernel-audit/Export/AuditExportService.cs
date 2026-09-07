using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit.Payloads;

namespace Harborline.Api.Kernel.Audit.Export;

/// <summary>
/// Orchestrates the AUDIT-EXPORT compensating control (enrollment Phase C control #2;
/// <c>project_sod_compensating_controls</c>) — reads a tenant's audit trail, delivers it to an INDEPENDENT
/// reviewer via the swappable <see cref="IAuditExportSink"/>, and records the export act ITSELF into the audit
/// trail (<see cref="AuditEventType.AuditExported"/>). The audit-stream is recorded in the audit-stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization is the caller's responsibility.</b> Triggering an export is gated by
/// <c>telemetry:export</c> (the audit-stream held tighter than operational telemetry, CIC 2026-06-20). This
/// service does NOT check the permission — the route/handler that invokes it does (<c>HasPermission(
/// Permission.TelemetryExport)</c>), exactly as the financial post path gates on <c>gl:post</c> before
/// reaching the posting service. Keeping the permission check at the boundary keeps this service a pure
/// orchestrator over the kernel-audit substrate.
/// </para>
/// <para>
/// <b>Reads through the read-side substrate.</b> Export uses <see cref="IAuditEventReader.StreamAsync"/> (the
/// un-paginated bulk surface) so the same tenant-scoping + uniform-empty discipline the rest of the read-side
/// honors applies; cross-tenant reads return nothing (ADR 0092 §A3).
/// </para>
/// <para>
/// <b>Fail-safe + the export act is ALWAYS recorded (#1295 F3).</b> A sink transport fault should be surfaced
/// by the sink's own telemetry, not propagated as a node-blocking exception (the local trail is authoritative;
/// export is additive). <see cref="IAuditExportSink.ExportAsync"/> is documented as fire-and-forget that MUST
/// NOT throw — but a NON-CONFORMANT external sink could deliver some records and THEN throw. This service
/// therefore wraps the sink call in try/catch: on a sink fault it still records an <c>AuditExported</c> event
/// (with <see cref="EnrollmentCompensatingControlPayloads.AuditExportedPayload.Failed"/> = true and a
/// <see cref="EnrollmentCompensatingControlPayloads.AuditExportedPayload.FaultMessage"/>) so the export ACT is
/// auditable even when the destination misbehaves — closing the partial-export-WITHOUT-record window. On the
/// success path the recorded count reflects what the destination accepted.
/// </para>
/// </remarks>
public sealed class AuditExportService
{
    private readonly IAuditEventReader _reader;
    private readonly IAuditExportSink _sink;
    private readonly IAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _time;

    /// <summary>
    /// Construct over the read-side reader (source), the export sink (destination — bundled no-op or external
    /// reviewer/SIEM swap), the write-side trail + signer (to record the <c>AuditExported</c> event), and an
    /// optional clock.
    /// </summary>
    public AuditExportService(
        IAuditEventReader reader,
        IAuditExportSink sink,
        IAuditTrail trail,
        IOperationSigner signer,
        TimeProvider? time = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Export the tenant's audit trail (optionally windowed) to the independent reviewer, then record the
    /// export act in the trail. Returns the number of records the destination accepted.
    /// </summary>
    /// <param name="tenantId">The tenant whose audit-stream is exported (tenant-scoped read).</param>
    /// <param name="exporterPartyId">The party triggering the export (held <c>telemetry:export</c>).</param>
    /// <param name="from">Optional inclusive lower bound on <see cref="AuditRecord.OccurredAt"/>.</param>
    /// <param name="to">Optional inclusive upper bound on <see cref="AuditRecord.OccurredAt"/>.</param>
    /// <param name="correlationId">Optional correlation-id from the originating request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records the destination accepted.</returns>
    public async Task<int> ExportAsync(
        TenantId tenantId,
        string exporterPartyId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        if (tenantId == default)
        {
            throw new ArgumentException("tenantId is required for audit export.", nameof(tenantId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(exporterPartyId);

        // Read the tenant's audit-stream (un-paginated, tenant-scoped; cross-tenant returns nothing).
        var query = new AuditEventReaderQuery(From: from, To: to);
        var batch = new List<AuditRecord>();
        await foreach (var rec in _reader.StreamAsync(tenantId, query, ct).ConfigureAwait(false))
        {
            batch.Add(rec);
        }

        // Deliver to the independent reviewer / SIEM (bundled no-op by default; external = the swap).
        // #1295 F3 — the sink is documented as never-throw, but a NON-CONFORMANT external sink could deliver
        // some records and THEN throw. Wrap the call so a sink fault NEVER leaves a partial-export-WITHOUT-record:
        // we record the export ACT either way (success → accepted count; fault → Failed=true + the message), so
        // the export attempt is always auditable. A cancellation is re-thrown (cooperative shutdown, not a sink
        // contract breach).
        int accepted;
        Exception? sinkFault = null;
        try
        {
            accepted = await _sink.ExportAsync(batch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The sink violated its never-throw contract (or partially delivered then threw). Record the act as
            // FAILED rather than swallowing it silently — the opposite of leaking records with no AuditExported.
            sinkFault = ex;
            accepted = 0;
        }

        // Record the export act itself — the audit-stream is recorded in the audit-stream (always, even on a
        // sink fault — #1295 F3).
        var payload = new EnrollmentCompensatingControlPayloads.AuditExportedPayload(
            TenantId: tenantId,
            ExporterPartyId: exporterPartyId,
            DestinationLabel: _sink.DestinationLabel,
            RecordCount: accepted,
            From: from,
            To: to,
            CorrelationId: correlationId,
            Failed: sinkFault is not null,
            FaultMessage: sinkFault?.Message);

        var occurredAt = _time.GetUtcNow();
        var signed = await _signer
            .SignAsync(new AuditPayload(payload.ToBody()), occurredAt, Guid.NewGuid(), ct)
            .ConfigureAwait(false);
        await _trail.AppendAsync(
            new AuditRecord(
                AuditId: Guid.NewGuid(),
                TenantId: tenantId,
                EventType: AuditEventType.AuditExported,
                OccurredAt: occurredAt,
                Payload: signed,
                AttestingSignatures: ImmutableArray<AttestingSignature>.Empty),
            ct).ConfigureAwait(false);

        return accepted;
    }
}
