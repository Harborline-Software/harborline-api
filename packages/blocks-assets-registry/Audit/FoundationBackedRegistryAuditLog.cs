using System.Buffers;
using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;

using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Audit;

/// <summary>
/// The <b>durable host-swap</b> of <see cref="IRegistryAuditLog"/> onto the foundation audit
/// substrate (<see cref="IAuditLog"/>) — ADR 0101 Rev 3.1 Wave 2 gate <b>U1</b>.
/// </summary>
/// <remarks>
/// <para>
/// Wave 1 shipped the in-memory <see cref="InMemoryRegistryAuditLog"/> behind
/// <see cref="IRegistryAuditLog"/> with a documented production seam (the deep-review verdict named
/// U1 as the Wave-2 gate: "the durable <c>IRegistryAuditLog</c> host-swap MUST land before Wave 2
/// live capture ships to a customer"). This adapter closes that gate: every registry mutation now
/// rides the same hash-chained, append-only <see cref="IAuditLog"/> the forms engine and
/// entity/hierarchy flows use, instead of a process-local list. The in-memory implementation is kept
/// for tests and demos; a live host calls <c>AddDurableAssetRegistryAudit()</c>.
/// </para>
/// <para>
/// <b>Impedance bridge.</b> The Wave-1 <see cref="IRegistryAuditLog"/> contract is synchronous and
/// every store call site is fire-and-forget (the returned event is never consumed). The foundation
/// log is async but its in-memory + hash-chain implementation completes synchronously
/// (<see cref="InMemoryAuditLog.AppendAsync"/> returns a completed <see cref="Task"/> under a lock —
/// no I/O). The bridge therefore drives it via <c>GetAwaiter().GetResult()</c> /
/// <c>ToBlockingEnumerable</c>, which is safe for the synchronous-completing substrate. A future
/// fully-asynchronous durable backend (real DB) would motivate promoting
/// <see cref="IRegistryAuditLog"/> to an async contract; that is a tracked follow-up, not a Wave-2
/// blocker.
/// </para>
/// <para>
/// <b>Chain fidelity.</b> The per-<c>(tenant, subject)</c> chain isolation of the Wave-1 log is
/// preserved by keying each foundation chain on an <see cref="EntityId"/> whose local part binds
/// both the tenant and the subject, so two tenants (or two subjects) never share a hash chain. Every
/// read additionally re-asserts <c>record.Tenant == tenant</c> — fail-closed tenant isolation, never
/// a trusted key alone. The registry-specific op, subject, actor, and detail ride the audit payload
/// so a <see cref="RegistryAuditEvent"/> round-trips faithfully.
/// </para>
/// </remarks>
public sealed class FoundationBackedRegistryAuditLog : IRegistryAuditLog
{
    /// <summary>Chain-key scheme for a registry audit subject (opaque; never surfaced to callers).</summary>
    internal const string SubjectScheme = "assetreg";

    /// <summary>Chain-key authority for a registry audit subject.</summary>
    internal const string SubjectAuthority = "registry";

    // A control separator (US, unit-separator U+001F) that does not appear in a tenant slug or a
    // GUID-backed subject id, so {tenant}{sep}{subject} is an unambiguous per-(tenant, subject) key.
    private const char KeySeparator = '';

    private readonly IAuditLog _inner;

    /// <summary>Wraps the foundation audit log the registry mutations will ride.</summary>
    public FoundationBackedRegistryAuditLog(IAuditLog inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public RegistryAuditEvent Append(
        TenantId tenant,
        string subject,
        RegistryOp op,
        Instant at,
        string? actorRef = null,
        string? detail = null)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var entity = SubjectEntity(tenant, subject);
        using var payload = BuildPayload(op, subject, actorRef, detail);
        var actor = string.IsNullOrWhiteSpace(actorRef) ? ActorId.System : new ActorId(actorRef);

        var id = _inner.AppendAsync(new AuditAppend(
            EntityId: entity,
            VersionId: null,
            Op: MapOp(op),
            Actor: actor,
            Tenant: tenant,
            At: at.Value,
            Payload: payload)).GetAwaiter().GetResult();

        // Faithful return: locate the record we just appended (matched by its allocated id) inside
        // the freshly-mapped chain so PreviousHash/Hash line up with the durable chain.
        foreach (var mapped in MapEntityChain(entity, tenant))
        {
            if (mapped.Sequence == id.Value)
            {
                return mapped;
            }
        }

        // Unreachable for a well-behaved log; degrade without fabricating a chain hash.
        return new RegistryAuditEvent(id.Value, tenant, subject, op, at, actorRef, detail, null, string.Empty);
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryAuditEvent> ForSubject(TenantId tenant, string subject)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return MapEntityChain(SubjectEntity(tenant, subject), tenant);
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryAuditEvent> ForTenant(TenantId tenant)
    {
        RegistryTenantGuard.Require(tenant);

        // Tenant=null on the query is system-scope (every entity's chain visible); filter to this
        // tenant in memory (fail-closed isolation), then rebuild each subject's chain so the
        // per-subject PreviousHash links are correct, and order the flattened result by sequence.
        var events = new List<RegistryAuditEvent>();
        foreach (var group in DrainAll().Where(r => r.Tenant.Equals(tenant)).GroupBy(r => r.EntityId))
        {
            string? previousHash = null;
            foreach (var record in group.OrderBy(r => r.At).ThenBy(r => r.Id.Value))
            {
                events.Add(MapRecord(record, previousHash));
                previousHash = record.Hash;
            }
        }
        return events.OrderBy(e => e.Sequence).ToList();
    }

    /// <inheritdoc />
    public bool VerifyChain(TenantId tenant, string subject)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return _inner.VerifyChainAsync(SubjectEntity(tenant, subject)).GetAwaiter().GetResult();
    }

    private IReadOnlyList<RegistryAuditEvent> MapEntityChain(EntityId entity, TenantId tenant)
    {
        var events = new List<RegistryAuditEvent>();
        string? previousHash = null;
        foreach (var record in Drain(entity))
        {
            // Belt-and-suspenders: the chain key already binds the tenant, but a read never trusts
            // the key alone — a foreign-tenant record on this chain (impossible by construction) is
            // dropped rather than surfaced.
            if (!record.Tenant.Equals(tenant))
            {
                previousHash = record.Hash;
                continue;
            }
            events.Add(MapRecord(record, previousHash));
            previousHash = record.Hash;
        }
        return events;
    }

    private IEnumerable<AuditRecord> Drain(EntityId entity)
        => _inner.QueryAsync(new AuditQuery(Entity: entity)).ToBlockingEnumerable();

    private IEnumerable<AuditRecord> DrainAll()
        => _inner.QueryAsync(new AuditQuery()).ToBlockingEnumerable();

    private static EntityId SubjectEntity(TenantId tenant, string subject)
        => new(SubjectScheme, SubjectAuthority, tenant.Value + KeySeparator + subject);

    private static RegistryAuditEvent MapRecord(AuditRecord record, string? previousHash)
    {
        var root = record.Payload.RootElement;
        var op = (RegistryOp)root.GetProperty("registryOp").GetInt32();
        var subject = root.GetProperty("subject").GetString()!;
        var actorRef = root.TryGetProperty("actorRef", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        return new RegistryAuditEvent(
            Sequence: record.Id.Value,
            Tenant: record.Tenant,
            Subject: subject,
            Op: op,
            At: new Instant(record.At),
            ActorRef: actorRef,
            Detail: detail,
            PreviousHash: previousHash,
            Hash: record.Hash);
    }

    private static JsonDocument BuildPayload(RegistryOp op, string subject, string? actorRef, string? detail)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "asset-registry-mutation");
            writer.WriteNumber("registryOp", (int)op);
            writer.WriteString("subject", subject);
            if (actorRef is not null)
            {
                writer.WriteString("actorRef", actorRef);
            }
            if (detail is not null)
            {
                writer.WriteString("detail", detail);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    /// <summary>
    /// Maps a registry mutation kind onto the closest canonical foundation <see cref="Op"/>. The
    /// precise <see cref="RegistryOp"/> is preserved verbatim in the audit payload (<c>registryOp</c>),
    /// so no fidelity is lost by the coarser canonical code.
    /// </summary>
    private static Op MapOp(RegistryOp op) => op switch
    {
        RegistryOp.EntityTypeCreated => Op.Mint,
        RegistryOp.EntityCreated => Op.Mint,
        RegistryOp.EntityTypeOverridden => Op.Write,
        RegistryOp.EntityUpdated => Op.Write,
        RegistryOp.RelationshipAdded => Op.Write,
        RegistryOp.RelationshipClosed => Op.Write,
        RegistryOp.ConditionAssessed => Op.Write,
        RegistryOp.EntityRetired => Op.Delete,
        // Wave 5 ([A11]) — both spatial ops map EXPLICITLY, never through the Op.Write fallthrough:
        // a mint silently classified as a write, or a rejection classified as a non-destructive
        // update, is a misclassification in the canonical code even though the payload preserves
        // registryOp verbatim.
        RegistryOp.SpatialFrameDescriptorMinted => Op.Mint,
        RegistryOp.SpatialFrameDescriptorConflictDetected => Op.Reject,
        // Audit@Read (store-level pii enforcement): a governed-cell unsealing is a canonical Read
        // — recording is opt-in per schema and this family opts in; never the Write fallthrough.
        RegistryOp.SpatialFrameDescriptorPiiUnsealed => Op.Read,
        _ => Op.Write,
    };
}
