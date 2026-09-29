using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// T-986: the host's kernel audit record store, in <c>local-node.db</c>. It persists each record exactly as
/// given, authority snapshot included; <see cref="AuthorityCapturingAuditTrail"/> wraps it as the host's
/// <see cref="IAuditTrail"/>, and the one reader reads it through <see cref="SnapshotAsync"/>. It replaces the
/// in-memory trail the host shipped, which lost every record on restart. The audit of an enrollment change or
/// an authorization write is durable before it gets here: it commits in the write's own transaction as an
/// <see cref="NodeAuditOutbox"/> entry, and the outbox delivers it.
/// </summary>
public sealed class NodeAuditTrailStore(IDbContextFactory<NodeLocalSearchDbContext> factory) : IAuditTrail
{
    /// <inheritdoc />
    public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.TenantId == default)
            throw new ArgumentException("AuditRecord.TenantId must be non-default per IMustHaveTenant.", nameof(record));
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuditTrail.Add(new AuditTrailRow
        {
            AuditId = record.AuditId.ToString("D"),
            TenantId = record.TenantId.Value,
            EventType = record.EventType.Value,
            OccurredAt = record.OccurredAt,
            RecordJson = NodeAuditRecordJson.Write(record),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AuditRecord> QueryAsync(
        AuditQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<AuditTrailRow> rows;
        await using (var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var tenant = query.TenantId.Value;
            var matching = db.AuditTrail.AsNoTracking().Where(row => row.TenantId == tenant);
            if (query.AuditId is { } auditId)
            {
                var id = auditId.ToString("D");
                matching = matching.Where(row => row.AuditId == id);
            }
            if (query.EventType is { } eventType)
                matching = matching.Where(row => row.EventType == eventType.Value);
            rows = await matching.ToListAsync(ct).ConfigureAwait(false);
        }

        // SQLite stores the instant as text, so the time filters and the order run here.
        foreach (var row in rows.OrderBy(row => row.OccurredAt))
        {
            ct.ThrowIfCancellationRequested();
            if (query.OccurredAfter is { } after && row.OccurredAt < after) continue;
            if (query.OccurredBefore is { } before && row.OccurredAt > before) continue;
            var record = NodeAuditRecordJson.Read(row.RecordJson);
            if (query.IssuedBy is { } issuer && !record.Payload.IssuerId.Equals(issuer)) continue;
            yield return record;
        }
    }

    /// <summary>Every stored record, for the reader.</summary>
    /// <remarks>ponytail: reads the whole table per call; page in SQL when a node's trail outgrows memory.</remarks>
    public async ValueTask<IReadOnlyList<AuditRecord>> SnapshotAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AuditTrail.AsNoTracking().Select(row => row.RecordJson).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(NodeAuditRecordJson.Read).ToArray();
    }
}

/// <summary>
/// T-986: the stored form of a kernel <see cref="AuditRecord"/> and of its signed payload envelope. A read
/// returns the body's values as plain strings, numbers, booleans, arrays (string arrays when every item is a string) and maps, so the envelope's canonical
/// bytes, and therefore its signature, are the ones that were signed.
/// </summary>
internal static class NodeAuditRecordJson
{
    private sealed record Stored(
        SignedOperation<AuditPayload> Payload,
        AttestingSignature[] AttestingSignatures,
        int FormatVersion,
        AuthoritySnapshot? AuthoritySnapshot,
        string? Actor,
        string? TargetKind,
        string? TargetId,
        string? TargetScope,
        string? Act);

    private sealed record StoredRecord(Guid AuditId, string TenantId, string EventType, DateTimeOffset OccurredAt, Stored Body);

    internal static string Write(AuditRecord record) => JsonSerializer.Serialize(new StoredRecord(
        record.AuditId,
        record.TenantId.Value,
        record.EventType.Value,
        record.OccurredAt,
        new Stored(
            record.Payload,
            [.. record.AttestingSignatures],
            record.FormatVersion,
            record.AuthoritySnapshot,
            record.Actor?.Value,
            record.Target?.RecordKind,
            record.Target?.RecordId,
            record.Target?.Scope.Value,
            record.Act?.ToString())));

    internal static AuditRecord Read(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredRecord>(json)
            ?? throw new InvalidOperationException("A stored audit record is empty.");
        var body = stored.Body;
        return new AuditRecord(
            stored.AuditId,
            new TenantId(stored.TenantId),
            new AuditEventType(stored.EventType),
            stored.OccurredAt,
            Plain(body.Payload),
            body.AttestingSignatures,
            body.FormatVersion,
            body.AuthoritySnapshot,
            body.Actor is null ? (ActorId?)null : new ActorId(body.Actor),
            body.TargetKind is null
                ? (AuthorizationTarget?)null
                : new AuthorizationTarget(body.TargetKind, body.TargetId!, ScopeExpression.Parse(body.TargetScope!)),
            body.Act is null ? (PermissionAtom?)null : PermissionAtom.Parse(body.Act));
    }

    internal static string WritePayload(SignedOperation<AuditPayload> payload) => JsonSerializer.Serialize(payload);

    internal static SignedOperation<AuditPayload> ReadPayload(string json) =>
        Plain(JsonSerializer.Deserialize<SignedOperation<AuditPayload>>(json)
            ?? throw new InvalidOperationException("A stored audit payload is empty."));

    private static SignedOperation<AuditPayload> Plain(SignedOperation<AuditPayload> payload) => payload with
    {
        Payload = new AuditPayload(payload.Payload.Body.ToDictionary(
            pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal)),
    };

    private static object? Plain(object? value) => value is not JsonElement element ? value : element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => Items(element.EnumerateArray().Select(item => Plain(item)).ToArray()),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name, property => Plain(property.Value), StringComparer.Ordinal),
        _ => null,
    };

    // A list of strings (granted permissions) reads back as one, as the recorder wrote it.
    private static object Items(object?[] items) =>
        items.All(item => item is string) ? items.Cast<string>().ToArray() : items;
}
