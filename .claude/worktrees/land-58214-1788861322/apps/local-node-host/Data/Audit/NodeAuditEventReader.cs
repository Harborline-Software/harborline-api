using System.Buffers.Text;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Node read-model over the recoverable <c>node_audit_events</c> table — the node-side audit read
/// surface that repoints ADR 0094's <c>IAuditEventReader</c> off the seed-keyed kernel
/// <c>IEventLog</c> onto the SC-4-recoverable <c>local-node.db</c> (ADR 0126 §D3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-native surface (deliberate).</b> The kernel <c>IAuditEventReader</c> contract returns the
/// kernel <c>AuditRecord</c>, whose <c>Payload</c> is a <c>SignedOperation&lt;AuditPayload&gt;</c>
/// envelope (Ed25519-locked, seed-derived). The node v1 audit rows are UNSIGNED (no node signer) and
/// carry the offline <c>signature_state</c> the wire needs — neither maps cleanly onto that envelope.
/// So this reader exposes a node-native list/detail surface returning <see cref="NodeAuditEventView"/>
/// (the exact <c>audit-events.ts</c> wire shape + the computed <c>signature_state</c>), which is what
/// the node route serves. This faithfully repoints the node READ path to the recoverable store (the
/// ADR's intent) without synthesising a fake signed envelope for the kernel contract type — a
/// fragility the ADR's substrate-impl-insulation framing (contract unchanged, persistence binding
/// moves) does not require us to incur, since the node has no kernel-<c>AuditRecord</c> consumer.
/// </para>
/// <para>
/// <b>Tenant scoping.</b> Every query takes <c>tenantId</c> as the first parameter and filters with an
/// explicit <c>WHERE TenantId</c> — the node's defence-in-depth boundary (ADR 0092; no ambient query
/// filter). The tenant is the active-team-derived tenant the caller resolves via
/// <c>NodeTenant.Resolve(activeTeam)</c> / the ambient <c>ActiveTeamTenantContext</c> (ADR 0032 identity
/// layer); switching the active org switches which org's audit rows a query reads — the explicit
/// <c>WHERE TenantId</c> is the per-org isolation predicate, no longer a fixed <c>"local"</c> sentinel.
/// </para>
/// <para>
/// <b>Offline integrity.</b> <c>signature_state</c> is computed at read time via
/// <see cref="NodeAuditSignatureClassifier"/> (the offline, key-independent <c>HashChain</c> verdict +
/// the sealed pre-reseed epoch logic), so it works fully offline and survives a passphrase reseed.
/// </para>
/// <para>
/// <b>Cursor.</b> Opaque base64url of <c>(OccurredAt, AuditId, TenantId)</c>. NOT IOperationSigner-
/// signed — the node is loopback-only, single-tenant, no auth (ADR 0114/0115), so the Bridge's
/// signed-cursor cross-tenant-replay threat does not apply; the tenant field is still carried + checked
/// for parity with the Bridge contract.
/// </para>
/// </remarks>
public sealed class NodeAuditEventReader
{
    /// <summary>Default page size when the caller omits one.</summary>
    public const int DefaultPageSize = 50;
    /// <summary>Hard upper bound on page size.</summary>
    public const int MaxPageSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly NodeAuditSignatureVerificationContext? _verification;

    /// <summary>
    /// Construct bound to the recoverable local-node EF context factory, with an optional per-event
    /// signature-verification context (ADR 0135 — the per-event signed event-log PASS-gate).
    /// </summary>
    /// <param name="contextFactory">The recoverable <c>local-node.db</c> EF context factory.</param>
    /// <param name="verification">
    /// Optional verification context (the current node issuer + the foundation
    /// <c>IOperationVerifier</c>). When supplied, signed rows are REALLY Ed25519-re-verified at read time
    /// (a tampered signed row reads <c>VerificationFailed</c>). When null (the SC4 guard path / any build
    /// without the node verifier wired), the classifier keeps the conservative chain-only posture and a
    /// signed + chain-intact row reads <c>Verified</c> without re-checking the signature.
    /// </param>
    public NodeAuditEventReader(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        NodeAuditSignatureVerificationContext? verification = null)
    {
        _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));
        _verification = verification;
    }

    /// <summary>
    /// Paginated reverse-chronological list (OccurredAt DESC, AuditId DESC) for <paramref name="tenantId"/>.
    /// </summary>
    public async Task<NodeAuditEventPage> ListAsync(
        string tenantId,
        NodeAuditEventReaderQuery query,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        // Cross-tenant cursor reuse → uniform-empty (parity with the Bridge ADR 0092 §A3 posture).
        if (query.Cursor is { } c && !string.Equals(c.TenantId, tenantId, StringComparison.Ordinal))
        {
            return new NodeAuditEventPage(Array.Empty<NodeAuditEventView>(), null, false);
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var q = ctx.Set<NodeAuditEventRow>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId);

        if (query.EventType is { Length: > 0 } et)
        {
            q = q.Where(r => r.EventType == et);
        }
        if (query.From is { } from)
        {
            q = q.Where(r => r.OccurredAt >= from);
        }
        if (query.To is { } to)
        {
            q = q.Where(r => r.OccurredAt <= to);
        }
        if (query.CorrelationId is { Length: > 0 } cid)
        {
            q = q.Where(r => r.CorrelationId == cid);
        }
        if (query.Cursor is { } cursor)
        {
            // Tuple walk: R included iff R.OccurredAt < C.OccurredAt
            //   OR (R.OccurredAt == C.OccurredAt AND R.AuditId < C.AuditId). The AuditId tie-breaker
            //   uses the EF-translatable string.Compare(a, b) form (NOT the StringComparison.Ordinal
            //   overload, which EF cannot translate). SQLite's default BINARY collation on the AuditId
            //   column is ordinal, matching the GUID-D total order.
            var cursorAuditId = cursor.AuditId;
            q = q.Where(r =>
                r.OccurredAt < cursor.OccurredAt ||
                (r.OccurredAt == cursor.OccurredAt &&
                 string.Compare(r.AuditId, cursorAuditId) < 0));
        }

        var ordered = q
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.AuditId);

        // Take one extra to detect HasMore.
        var rows = await ordered.Take(pageSize + 1).ToListAsync(ct).ConfigureAwait(false);
        var hasMore = rows.Count > pageSize;
        var pageRows = hasMore ? rows.Take(pageSize).ToList() : rows;

        var epochs = await NodeAuditSignatureClassifier.LoadEpochsAsync(ctx, ct).ConfigureAwait(false);

        // Presentation order is business time, while the hash chain is physical append order. An older
        // admitted act may be appended later, and equal instants are broken by a random audit id, so no
        // page neighbour can safely stand in for the physical predecessor. Resolve every predecessor by
        // rowid over the unfiltered tenant chain before classifying.
        var views = new List<NodeAuditEventView>(pageRows.Count);
        for (var i = 0; i < pageRows.Count; i++)
        {
            var row = pageRows[i];
            var prevHash = await ResolvePrevHashAsync(ctx, tenantId, row, ct).ConfigureAwait(false);
            views.Add(ToView(row, NodeAuditSignatureClassifier.Classify(row, prevHash, epochs, _verification)));
        }

        string? nextCursor = null;
        if (hasMore && pageRows.Count > 0)
        {
            var last = pageRows[^1];
            nextCursor = EncodeCursor(new NodeAuditCursor(last.OccurredAt, last.AuditId, tenantId));
        }

        return new NodeAuditEventPage(views, nextCursor, hasMore);
    }

    /// <summary>Single record by id, scoped to <paramref name="tenantId"/>. Null when not-found OR
    /// cross-tenant (uniform-empty per ADR 0092 §A3 — no diagnostic leak).</summary>
    public async Task<NodeAuditEventView?> GetByIdAsync(
        string tenantId,
        string auditId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(auditId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Set<NodeAuditEventRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.AuditId == auditId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var epochs = await NodeAuditSignatureClassifier.LoadEpochsAsync(ctx, ct).ConfigureAwait(false);
        var prevHash = await ResolvePrevHashAsync(ctx, tenantId, row, ct).ConfigureAwait(false);
        return ToView(row, NodeAuditSignatureClassifier.Classify(row, prevHash, epochs, _verification));
    }

    /// <summary>
    /// Decodes a wire cursor string. Returns null on any malformed/empty input (the route maps that to
    /// a 400 invalid_cursor, matching the Bridge contract).
    /// </summary>
    public static NodeAuditCursor? TryDecodeCursor(string? wire)
    {
        if (string.IsNullOrEmpty(wire))
        {
            return null;
        }
        try
        {
            var encoded = Encoding.UTF8.GetBytes(wire);
            var decoded = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
            if (Base64Url.DecodeFromUtf8(encoded, decoded, out _, out var written) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }
            var json = Encoding.UTF8.GetString(decoded, 0, written);
            var dto = JsonSerializer.Deserialize<CursorDto>(json, JsonOptions);
            if (dto is null || string.IsNullOrEmpty(dto.AuditId) || string.IsNullOrEmpty(dto.TenantId))
            {
                return null;
            }
            if (!DateTimeOffset.TryParse(dto.OccurredAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                return null;
            }
            return new NodeAuditCursor(at, dto.AuditId, dto.TenantId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string EncodeCursor(NodeAuditCursor cursor)
    {
        var dto = new CursorDto
        {
            OccurredAt = cursor.OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            AuditId = cursor.AuditId,
            TenantId = cursor.TenantId,
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var encoded = new byte[Base64Url.GetEncodedLength(bytes.Length)];
        Base64Url.EncodeToUtf8(bytes, encoded, out _, out var written);
        return Encoding.UTF8.GetString(encoded, 0, written);
    }

    /// <summary>
    /// Resolves the hash of <paramref name="row"/>'s immediate predecessor in append order. Business
    /// <c>OccurredAt</c> values may move backwards and therefore cannot define hash-chain order.
    /// </summary>
    private static async Task<string?> ResolvePrevHashAsync(
        LocalNodeDbContext ctx,
        string tenantId,
        NodeAuditEventRow row,
        CancellationToken ct)
    {
        var rowAuditId = row.AuditId;
        var prev = await ctx.Set<NodeAuditEventRow>()
            .FromSql($"""
                SELECT candidate.*
                FROM node_audit_events AS candidate
                WHERE candidate."TenantId" = {tenantId}
                  AND candidate.rowid < (
                      SELECT current.rowid
                      FROM node_audit_events AS current
                      WHERE current."AuditId" = {rowAuditId})
                ORDER BY candidate.rowid DESC
                LIMIT 1
                """)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return prev?.Hash;
    }

    private static NodeAuditEventView ToView(NodeAuditEventRow row, string signatureState)
    {
        IReadOnlyDictionary<string, object?> summary;
        try
        {
            summary = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.Payload, JsonOptions)
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            summary = new Dictionary<string, object?>();
        }

        return new NodeAuditEventView(
            AuditId: row.AuditId,
            OccurredAt: row.OccurredAt.ToString("O"),
            EventType: row.EventType,
            Actor: row.Actor,
            CorrelationId: row.CorrelationId,
            TenantId: row.TenantId,
            PayloadSummary: summary,
            SignatureState: signatureState);
    }

    private sealed class CursorDto
    {
        public string OccurredAt { get; set; } = string.Empty;
        public string AuditId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }
}
