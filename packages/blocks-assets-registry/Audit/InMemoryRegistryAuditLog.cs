using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Audit;

/// <summary>
/// Thread-safe in-memory <see cref="IRegistryAuditLog"/> for tests, demos, and Wave-1 substrate
/// wiring. Hash-chains events per <c>(tenant, subject)</c> with SHA-256, mirroring the foundation
/// <c>IAuditLog</c> discipline. Persistence-backed implementations live behind the same interface.
/// </summary>
public sealed class InMemoryRegistryAuditLog : IRegistryAuditLog
{
    private readonly object _gate = new();
    private readonly List<RegistryAuditEvent> _events = new();
    private long _sequence;

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

        lock (_gate)
        {
            var previousHash = _events
                .Where(e => e.Tenant.Equals(tenant) && string.Equals(e.Subject, subject, StringComparison.Ordinal))
                .Select(e => e.Hash)
                .LastOrDefault();

            var sequence = ++_sequence;
            var hash = ComputeHash(sequence, tenant, subject, op, at, actorRef, detail, previousHash);

            var record = new RegistryAuditEvent(
                sequence, tenant, subject, op, at, actorRef, detail, previousHash, hash);
            _events.Add(record);
            return record;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryAuditEvent> ForSubject(TenantId tenant, string subject)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        lock (_gate)
        {
            return _events
                .Where(e => e.Tenant.Equals(tenant) && string.Equals(e.Subject, subject, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryAuditEvent> ForTenant(TenantId tenant)
    {
        RegistryTenantGuard.Require(tenant);

        lock (_gate)
        {
            return _events.Where(e => e.Tenant.Equals(tenant)).ToList();
        }
    }

    /// <inheritdoc />
    public bool VerifyChain(TenantId tenant, string subject)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        lock (_gate)
        {
            string? previousHash = null;
            foreach (var e in _events.Where(e =>
                         e.Tenant.Equals(tenant) && string.Equals(e.Subject, subject, StringComparison.Ordinal)))
            {
                if (!string.Equals(e.PreviousHash, previousHash, StringComparison.Ordinal))
                {
                    return false;
                }

                var expected = ComputeHash(e.Sequence, e.Tenant, e.Subject, e.Op, e.At, e.ActorRef, e.Detail, e.PreviousHash);
                if (!string.Equals(e.Hash, expected, StringComparison.Ordinal))
                {
                    return false;
                }

                previousHash = e.Hash;
            }

            return true;
        }
    }

    private static string ComputeHash(
        long sequence,
        TenantId tenant,
        string subject,
        RegistryOp op,
        Instant at,
        string? actorRef,
        string? detail,
        string? previousHash)
    {
        // Canonical form: length-prefixed fields so no field boundary can collide with another
        // (subject "a" + detail "bc" must not hash the same as subject "ab" + detail "c").
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string Field(string? v) => (v ?? string.Empty).Length + ":" + (v ?? string.Empty);
        var canonical = string.Concat(
            Field(sequence.ToString(inv)),
            Field(tenant.Value),
            Field(subject),
            Field(((int)op).ToString(inv)),
            Field(at.Value.ToString("O")),
            Field(actorRef),
            Field(detail),
            Field(previousHash));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}
