using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Default <see cref="INodeAuditWriteEnlister"/>. Stages a <c>Financial.JournalPosted</c> audit row
/// onto the JE write's <see cref="LocalNodeDbContext"/> so the two commit in one SQLite transaction
/// (ADR 0126 §D2 / OQ2 = ATOMIC).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-event signing (ADR 0135 — the pre-multi-device PASS-gate; SEC-A2).</b> When an
/// <see cref="IOperationSigner"/> is supplied (the node's <c>RootSeedHex</c>→Ed25519 identity via
/// <c>NodePrincipalSigner.Signer</c>, derived under the ADR 0118 custody ladder), each JE-posted audit
/// row is SIGNED in addition to hash-chained: the signature covers the
/// <see cref="SignedOperation{T}"/> envelope assembled by <see cref="NodeAuditSignaturePayload"/>
/// (payload = the row's canonical JSON; issuedAt = OccurredAt; nonce = the AuditId GUID; issuer = the
/// node identity). The 64 signature bytes fill <see cref="NodeAuditEventRow.Signature"/>, so the reader
/// surfaces the row as <c>signature_state = Verified</c> (ADR 0049 <c>v1</c> format). This rides the
/// SAME atomic enlister transaction — sign-then-stage, committed by the caller's single
/// <c>SaveChangesAsync</c>; there is NO second write. Nothing here invents crypto — it reuses the
/// audited foundation Ed25519 signer the node already wires for principal-signature signing.
/// </para>
/// <para>
/// <b>Unsigned remains valid (v0 backward-compat).</b> When NO signer is supplied (every pre-T4
/// composition, the SC4-T9(b) guard providers, and any node build without the foundation signer wired),
/// the row carries a null <see cref="NodeAuditEventRow.Signature"/> exactly as before. The reader
/// surfaces these as <c>signature_state = NotSigned</c> paired with a <c>Verified</c> <c>HashChain</c>
/// result — the "chain-intact, not cryptographically signed" historical state ADR 0126 §D4 describes
/// (NEVER <c>VerificationFailed</c>). Signing is additive: the hash chain is the v0 integrity
/// guarantee; the signature is the v1 authenticity addition cross-device sync needs.
/// </para>
/// <para>
/// <b>Hash chaining within the transaction.</b> The chain tip (the tenant's latest appended row by
/// SQLite <c>rowid</c>) is read from the SAME <paramref name="ctx"/> the caller is
/// about to save — including any rows staged earlier in this unit-of-work — so the new row's
/// <c>PrevHash</c> links correctly even within a multi-write transaction. Reads are tracked
/// (no <c>AsNoTracking</c>) so an already-staged-but-unsaved tip is visible; SQLite serialises writes
/// so concurrent chain races are not a single-device concern.
/// </para>
/// </remarks>
public sealed class NodeAuditWriteEnlister : INodeAuditWriteEnlister, IWriteEnlistment
{
    /// <summary>The canonical event-type string for a posted journal entry (matches the frontend
    /// <c>KNOWN_AUDIT_EVENT_TYPES</c> constant <c>'Financial.JournalPosted'</c>).</summary>
    public const string JournalPostedEventType = "Financial.JournalPosted";

    /// <summary>The audit attribution shape sourced exclusively from the carried authorization decision.</summary>
    public const string CarriedDecisionAttributionSchema = "carried-authorization-decision/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IOperationSigner? _signer;

    /// <inheritdoc />
    public WriteInvariant Invariant => NodeWriteInvariants.Audit;

    /// <inheritdoc />
    public async ValueTask<WriteEnlistmentOutcome> EnlistAsync(
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        if (unitOfWork is not NodeJournalWriteUnitOfWork nodeWrite)
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        await EnlistJournalPostedAsync(
                nodeWrite.Context, nodeWrite.Entry, nodeWrite.Decision, cancellationToken)
            .ConfigureAwait(false);
        return WriteEnlistmentOutcome.Enlisted;
    }

    /// <summary>
    /// Constructs the enlister with, optionally, the node's per-event <see cref="IOperationSigner"/>.
    /// </summary>
    /// <param name="signer">
    /// Optional node signer (the <c>RootSeedHex</c>→Ed25519 identity; ADR 0118 custody ladder). When
    /// supplied, each JE-posted audit row is per-event signed (ADR 0135 PASS-gate, ADR 0049 <c>v1</c>).
    /// When null, rows are written unsigned — the v0 historical state, valid by the hash chain alone.
    /// </param>
    public NodeAuditWriteEnlister(IOperationSigner? signer = null)
    {
        _signer = signer;
    }

    /// <inheritdoc />
    public async Task EnlistJournalPostedAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
            entry.TenantId,
            "journal-entry",
            entry.Id.Value);

        var tenantId = entry.TenantId.Value;
        // Normalise to the round-trip-stable UTC form the OccurredAt value-converter stores, so the
        // hash computed here matches the hash recomputed at read time over the read-back value (the
        // ISO-8601 "O" string round-trips losslessly through DateTimeOffset.Parse). Without this, a
        // sub-tick difference between the live value and the persisted/parsed value could break the
        // chain verification on read.
        var occurredAt = DateTimeOffset.Parse(
            decision.Request.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal);
        var auditId = Guid.NewGuid().ToString("D");
        var correlationId = System.Diagnostics.Activity.Current?.Id;

        var actor = decision.Request.Principal.Value;

        // The node's attesting Ed25519 public key (the CRYPTO PrincipalId / signer IssuerId, base64url)
        // — the key bound to the acting Party INSIDE the signed envelope (F4/F5). Null on an unsigned
        // (v0) build; the binding then degrades to hash-chain integrity, never a false VerificationFailed.
        var attestingPublicKey = _signer?.IssuerId.ToBase64Url();

        var payloadJson = BuildPayloadJson(entry, correlationId, decision, attestingPublicKey);

        // Chain order is append order, not business-time order. An admitted act can legitimately carry an
        // older OccurredAt than the prior append; ordering by OccurredAt would then fork the hash chain.
        var tip = await ctx.Set<NodeAuditEventRow>()
            .FromSql($"""
                SELECT * FROM node_audit_events
                WHERE "TenantId" = {tenantId}
                ORDER BY rowid DESC
                LIMIT 1
                """)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var prevHash = tip?.Hash;
        var hash = NodeAuditHashChain.ComputeHash(
            prevHash, auditId, JournalPostedEventType, actor, tenantId, occurredAt, payloadJson);

        // ADR 0135 per-event signing (the pre-multi-device PASS-gate). When the node signer is wired,
        // sign the SignedOperation<string> envelope NodeAuditSignaturePayload assembles (payload =
        // canonical JSON; issuedAt = OccurredAt; nonce = the AuditId GUID; issuer = the node identity)
        // and persist the 64 raw signature bytes → v1 (Verified). No signer ⇒ null ⇒ v0 (NotSigned).
        // Signing happens inline here, BEFORE the row is staged, so it rides the SAME transaction the
        // caller commits — never a second write.
        byte[]? signature = null;
        if (_signer is not null)
        {
            var signed = await _signer
                .SignAsync(payloadJson, occurredAt, NodeAuditSignaturePayload.NonceFor(auditId), ct)
                .ConfigureAwait(false);
            signature = signed.Signature.AsSpan().ToArray();
        }

        var row = new NodeAuditEventRow
        {
            AuditId = auditId,
            TenantId = tenantId,
            EventType = JournalPostedEventType,
            OccurredAt = occurredAt,
            Actor = actor,
            CorrelationId = correlationId,
            Payload = payloadJson,
            PrevHash = prevHash,
            Hash = hash,
            // v1: per-event Ed25519 signature when the node signer is wired (ADR 0135 PASS-gate);
            // v0: null when unsigned — valid by the hash chain alone, surfaces as NotSigned.
            Signature = signature,
        };

        // STAGE only — the caller's single SaveChangesAsync commits this with the JE row atomically.
        ctx.Set<NodeAuditEventRow>().Add(row);
    }

    private static string BuildPayloadJson(
        JournalEntry entry,
        string? correlationId,
        AuthorizationDecision decision,
        string? attestingPublicKey)
    {
        decimal debitTotal = 0m;
        foreach (var line in entry.Lines)
        {
            debitTotal += line.Debit;
        }

        var body = new Dictionary<string, object?>
        {
            ["entity_type"] = "JournalEntry",
            ["entity_id"] = entry.Id.Value,
            ["tenant_id"] = entry.TenantId.Value,
            ["memo"] = entry.Memo,
            ["entry_date"] = entry.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["line_count"] = entry.Lines.Count,
            ["amount"] = debitTotal,
            ["source_reference"] = entry.SourceReference,
            ["source_kind"] = entry.SourceKind.ToString(),
            ["account_ids"] = entry.Lines.Select(l => l.AccountId.Value).Distinct().ToArray(),
            ["correlation_id"] = correlationId,
            ["attribution"] = new Dictionary<string, object?>
            {
                ["schema"] = CarriedDecisionAttributionSchema,
                ["member_party_id"] = decision.Request.Principal.Value,
                ["attesting_public_key"] = attestingPublicKey,
            },
            ["authority"] = new Dictionary<string, object?>
            {
                ["principal"] = decision.Request.Principal.Value,
                ["tenant"] = decision.Request.Tenant.Value,
                ["instant"] = decision.DecidedAt,
                ["act"] = decision.Request.Act.ToString(),
                ["target"] = new Dictionary<string, object?>
                {
                    ["record_kind"] = decision.Request.Target.RecordKind,
                    ["record_id"] = decision.Request.Target.RecordId,
                    ["scope"] = decision.Request.Target.Scope.ToString(),
                },
                ["grants"] = decision.Derivations.Select(item => new Dictionary<string, object?>
                {
                    ["grant_id"] = item.GrantId,
                    ["owner_version"] = item.GrantOwnerVersion,
                }).DistinctBy(item => $"{item["grant_id"]}\u001f{item["owner_version"]}").ToArray(),
                ["derivation_ids"] = decision.Derivations
                    .Select(item => item.DefinitionId).Distinct(StringComparer.Ordinal).ToArray(),
                ["resolution"] = decision.Resolution.Select(step => new Dictionary<string, object?>
                {
                    ["stage"] = step.Stage.ToString(),
                    ["inputs"] = step.Inputs.ToArray(),
                    ["outputs"] = step.Outputs.ToArray(),
                }).ToArray(),
            },
        };

        return JsonSerializer.Serialize(body, JsonOptions);
    }
}
