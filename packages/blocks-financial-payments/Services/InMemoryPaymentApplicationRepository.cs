using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

/// <summary>
/// In-memory <see cref="IPaymentApplicationRepository"/>. Thread-safe via
/// <c>ConcurrentDictionary</c>.
///
/// <para>
/// <b>Balance updates are NOT performed here.</b> This repository is a pure
/// storage layer. <c>IPaymentApplicationService</c> is responsible for
/// updating <see cref="Payment.UnappliedAmount"/>, Invoice/Bill balances, and
/// status fields after adding or reversing application records.
/// </para>
///
/// <para>
/// <b>Cohort-2 PR 0c tenant-keying retrofit.</b> Every <c>Get*</c> /
/// <c>List*</c> / <c>Reverse</c> filters by the <c>tenantId</c> argument;
/// cross-tenant access returns null / empty / false (uniform-404) and emits
/// <c>AuditEventType.TenantBoundaryViolation</c> when audit emission is wired.
/// <c>AddAsync</c> asserts <c>application.TenantId == tenantId</c>; mismatch
/// throws <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public sealed class InMemoryPaymentApplicationRepository : IPaymentApplicationRepository
{
    private readonly ConcurrentDictionary<PaymentApplicationId, PaymentApplication> _applications = new();
    private readonly object _reversalGate = new();
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly TenantId _auditTenant;
    private readonly TimeProvider? _time;

    /// <summary>Creates the repository without audit emission (tests, demos).</summary>
    public InMemoryPaymentApplicationRepository()
    {
    }

    /// <summary>
    /// Creates the repository with audit emission wired through
    /// <paramref name="auditTrail"/> + <paramref name="signer"/>;
    /// <paramref name="auditTenant"/> is the tenant attribution applied to
    /// emitted records.
    /// </summary>
    public InMemoryPaymentApplicationRepository(
        IAuditTrail auditTrail,
        IOperationSigner signer,
        TenantId auditTenant,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(auditTrail);
        ArgumentNullException.ThrowIfNull(signer);
        if (auditTenant == default)
        {
            throw new ArgumentException("TenantId is required for audit emission.", nameof(auditTenant));
        }
        _auditTrail = auditTrail;
        _signer = signer;
        _auditTenant = auditTenant;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task AddAsync(
        TenantId tenantId, PaymentApplication application, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        if (application is null) throw new ArgumentNullException(nameof(application));
        if (!application.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"PaymentApplication '{application.Id.Value}' carries TenantId '{application.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(application));
        }
        if (!_applications.TryAdd(application.Id, application))
            throw new InvalidOperationException($"PaymentApplication '{application.Id.Value}' already exists.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<PaymentApplication?> GetAsync(
        TenantId tenantId, PaymentApplicationId id, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_applications.TryGetValue(id, out var app)) return null;
        if (!app.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, app.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return null;
        }
        return app;
    }

    /// <inheritdoc />
    public async Task<PaymentApplicationReversalResult> ReverseAsync(
        TenantId tenantId,
        PaymentApplicationId id,
        Instant reversedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_applications.TryGetValue(id, out var app)) return PaymentApplicationReversalResult.NotFound;
        if (!app.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, app.TenantId, reversedAtUtc.Value, cancellationToken).ConfigureAwait(false);
            return PaymentApplicationReversalResult.NotFound;
        }

        lock (_reversalGate)
        {
            if (!_applications.TryGetValue(id, out app) || !app.TenantId.Equals(tenantId))
            {
                return PaymentApplicationReversalResult.NotFound;
            }
            if (app.ReversesApplicationId is not null)
            {
                return PaymentApplicationReversalResult.NotFound;
            }
            if (app.ReversedByApplicationId is not null)
            {
                return PaymentApplicationReversalResult.AlreadyReversed(app);
            }

            var reversal = PaymentApplication.CreateReversal(app, reversedAtUtc);
            if (!_applications.TryAdd(reversal.Id, reversal))
            {
                throw new InvalidOperationException(
                    $"PaymentApplication reversal '{reversal.Id.Value}' already exists.");
            }

            var reversed = app.MarkReversed(reversal.Id, reversedAtUtc);
            _applications[id] = reversed;
            return PaymentApplicationReversalResult.Recorded(reversed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PaymentApplication>> ListByPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken = default)
    {
        var rows = _applications.Values
            .Where(a => a.TenantId.Equals(tenantId) && a.PaymentId == paymentId)
            .ToList();
        return Task.FromResult<IReadOnlyList<PaymentApplication>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PaymentApplication>> ListByTargetAsync(TenantId tenantId, string targetId, CancellationToken cancellationToken = default)
    {
        var rows = _applications.Values
            .Where(a => a.TenantId.Equals(tenantId) && string.Equals(a.TargetId, targetId, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult<IReadOnlyList<PaymentApplication>>(rows);
    }

    // ── Audit emission (ADR 0092 §A6 canonical payload shape — sec-eng SPOT-CHECK GREEN template) ──
    //
    // Mirror of cohort 2 PR 0a InMemoryInvoiceRepository payload shape:
    //   entity_type, entity_id, requested_tenant, actual_tenant, correlation_id
    private async ValueTask EmitTenantBoundaryViolationAsync(
        string entityId,
        TenantId requestedTenant,
        TenantId actualTenant,
        DateTimeOffset? admittedAt,
        CancellationToken ct)
    {
        if (_auditTrail is null || _signer is null) return;

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var payload = new AuditPayload(new Dictionary<string, object?>
        {
            ["entity_type"]       = "PaymentApplication",
            ["entity_id"]         = entityId,
            ["requested_tenant"]  = requestedTenant.Value,
            ["actual_tenant"]     = actualTenant.Value,
            ["correlation_id"]    = correlationId,
        });
        var occurredAt = admittedAt ?? throw new InvalidOperationException("An audit instant is required when audit emission is wired.");
        var signed = await _signer.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: _auditTenant,
            EventType: AuditEventType.TenantBoundaryViolation,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }
}
