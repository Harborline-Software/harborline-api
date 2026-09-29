using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// In-memory <see cref="IAuditEventReader"/> reference implementation.
/// Shares the in-memory backing store with <see cref="InMemoryAuditTrail"/>
/// via constructor injection, providing consistent read-after-write
/// behaviour in test fixtures and development hosts without maintaining two
/// parallel stores.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR 0094 (IAuditEventReader) + ADR 0091 (ITenantContext) + ADR 0092
/// (substrate tenant-keyed repository contract) + ADR 0049 (audit-trail
/// substrate write side).
/// </para>
///
/// <para>
/// <b>DI lifetime constraint (ADR 0094 Amendment 2.5).</b>
/// <see cref="InMemoryAuditEventReader"/> constructor-injects
/// <see cref="InMemoryAuditTrail"/> as the CONCRETE class so it can call
/// the internal <c>Snapshot()</c> method to access the writer's in-memory
/// backing field directly. The host MUST register
/// <see cref="InMemoryAuditTrail"/> as Scoped or Singleton — NOT Transient.
/// A Transient registration would give the reader a fresh, empty
/// <see cref="InMemoryAuditTrail"/> on each resolution, silently losing
/// every record appended via the writer's instance. The
/// <c>AddHarborlineKernelAuditReaderInMemory()</c> extension registers the
/// trail as Scoped; overrides MUST maintain Scoped or Singleton. See
/// <see cref="DependencyInjection.ServiceCollectionExtensions"/> for the
/// startup lifetime assertion.
/// </para>
///
/// <para>
/// <b>Audit emission is recursion-safe.</b> When
/// <see cref="GetByIdAsync"/> detects a cross-tenant probe it emits via the
/// write-side <see cref="IAuditTrail"/> injected as the <c>emitter</c>
/// constructor parameter — NOT by calling any method on itself. The
/// emitted record is later readable by callers with the correct tenant; it
/// is never read BY the emitter as part of the emission path.
/// </para>
///
/// <para>
/// <b>Restart-volatile.</b> Process restart loses all stored records.
/// </para>
/// </remarks>
public sealed class InMemoryAuditEventReader : IAuditEventReader
{
    private readonly SnapshotAuditEventReader _reader;

    /// <summary>
    /// Initialises the reader with the shared in-memory store.
    /// </summary>
    /// <param name="trail">
    /// The CONCRETE <see cref="InMemoryAuditTrail"/> instance shared with the
    /// write side. Must be the same DI-scope instance as the registered
    /// writer.
    /// </param>
    /// <param name="emitter">
    /// The write-side <see cref="IAuditTrail"/> used to emit
    /// <c>TenantBoundaryViolation</c> audit records on cross-tenant probes.
    /// Typically resolves to the same underlying
    /// <see cref="InMemoryAuditTrail"/> via the DI container.
    /// </param>
    /// <param name="signer">
    /// The operation signer used to produce signed audit payloads when
    /// emitting <c>TenantBoundaryViolation</c> records.
    /// </param>
    public InMemoryAuditEventReader(
        InMemoryAuditTrail trail,
        IAuditTrail emitter,
        IOperationSigner signer)
    {
        ArgumentNullException.ThrowIfNull(trail);
        _reader = new SnapshotAuditEventReader(
            _ => ValueTask.FromResult<IReadOnlyList<AuditRecord>>(trail.Snapshot()), emitter, signer);
    }

    /// <inheritdoc />
    public Task<AuditRecord?> GetByIdAsync(
        TenantId tenantId, Guid auditId, DateTimeOffset admittedAt, CancellationToken ct = default) =>
        _reader.GetByIdAsync(tenantId, auditId, admittedAt, ct);

    /// <inheritdoc />
    public Task<AuditEventPage> ListAsync(
        TenantId tenantId, AuditEventReaderQuery query, CancellationToken ct = default) =>
        _reader.ListAsync(tenantId, query, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<AuditRecord> StreamAsync(
        TenantId tenantId, AuditEventReaderQuery query, CancellationToken ct = default) =>
        _reader.StreamAsync(tenantId, query, ct);
}
