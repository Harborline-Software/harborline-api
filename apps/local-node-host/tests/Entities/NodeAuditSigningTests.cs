using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ADR 0135 — per-event signed node event-log (the binding pre-multi-device PASS-gate; SEC-A2 of the
/// 0135 council). Proves the node JE-posted audit rows are now per-event Ed25519-SIGNED (reusing the
/// audited foundation signer the node already derives from its <c>RootSeedHex</c> root identity under
/// the ADR 0118 custody ladder), that a signed row's signature really verifies, that a TAMPERED signed
/// row fails verification, that the signing rides the SAME atomic enlister transaction (no second
/// write), and that <c>NotSigned</c> historical rows stay valid (v0 backward-compat) — the ADR 0049
/// <c>v0</c>→<c>v1</c> format gate is the presence of the signature.
/// </summary>
public sealed class NodeAuditSigningTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _signer = null!;
    private IOperationVerifier _verifier = null!;
    private NodeAuditSignatureVerificationContext _verification = null!;

    private static readonly TenantId LocalTenantId = new("local");

    // A fixed, deterministic 32-byte node root seed (the RootSeedHex→Ed25519 identity, ADR 0118).
    private static byte[] FixedSeed()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(0xA0 + i);
        }
        return seed;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-audit-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "audit-signing-test.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _signer = new NodePrincipalSigner(FixedSeed());
        _verifier = new Ed25519Verifier();
        _verification = new NodeAuditSignatureVerificationContext(_signer.Signer.IssuerId, _verifier);
    }

    public async Task DisposeAsync()
    {
        _signer.Dispose();
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // The signing enlister — the node clock + the node's per-event IOperationSigner (ADR 0135).
    private NodeAuditWriteEnlister SigningEnlister() => new(_signer.Signer);

    // A reader that performs REAL Ed25519 re-verification of signed rows.
    private NodeAuditEventReader VerifyingReader() => new(_factory, _verification);

    [Fact(DisplayName = "Signed: a JE-posted audit row carries a per-event Ed25519 signature that VERIFIES (Verified) — the node signer fills the signature column")]
    public async Task PostedJournalEntry_IsSigned_AndVerifies()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-SIGN-1", 100m));

        // The persisted row carries a non-null 64-byte Ed25519 signature (v1, signed).
        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value)
            .ToListAsync());
        Assert.NotNull(row.Signature);
        Assert.Equal(Signature.LengthInBytes, row.Signature!.Length);

        // The signature really verifies under the node's public key + the reconstructed envelope.
        var op = NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature);
        Assert.NotNull(op);
        Assert.True(_verifier.Verify(op!), "the per-event signature must verify against the node public key");

        // The reader (with verification context) surfaces it as Verified.
        var page = await VerifyingReader().ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery());
        var view = Assert.Single(page.Events);
        Assert.Equal(NodeAuditSignatureClassifier.Verified, view.SignatureState);
    }

    [Fact(DisplayName = "Tamper (signature): a signed row whose SIGNATURE bytes are altered (chain still recomputes) fails Ed25519 verification → VerificationFailed")]
    public async Task TamperedSignature_FailsVerification()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-TAMPER-1", 250m));

        // Corrupt ONLY the signature bytes, leaving the payload + hash untouched, so the key-independent
        // hash chain still recomputes (the chain cannot catch a signature-only tamper). This isolates
        // the Ed25519 verification: the ONLY thing that can flag it is real signature re-verification.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var row = await ctx.Set<NodeAuditEventRow>().FirstAsync(r => r.TenantId == LocalTenantId.Value);
            var corrupted = (byte[])row.Signature!.Clone();
            corrupted[0] ^= 0xFF; // flip a byte — still 64 bytes, still parseable, but not a valid signature
            ctx.Entry(row).Property(r => r.Signature).CurrentValue = corrupted;
            await ctx.SaveChangesAsync();
        }

        // Sanity: the hash chain STILL verifies over the (untouched payload/hash) row — proving the
        // tamper is signature-only and the failure below is genuinely from Ed25519, not the chain.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var rows = await ctx.Set<NodeAuditEventRow>()
                .FromSql($"""
                    SELECT * FROM node_audit_events
                    WHERE "TenantId" = {LocalTenantId.Value}
                    ORDER BY rowid ASC
                    """)
                .ToListAsync();
            Assert.True(NodeAuditHashChain.Verify(rows), "hash chain must still verify (signature-only tamper)");
        }

        // The verifying reader detects the bad signature on this CURRENT-epoch row → VerificationFailed.
        var page = await VerifyingReader().ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery());
        var view = Assert.Single(page.Events);
        Assert.Equal(NodeAuditSignatureClassifier.VerificationFailed, view.SignatureState);
    }

    [Fact(DisplayName = "Tamper (payload): mutating a signed row's payload breaks the hash chain → VerificationFailed (chain is the payload-integrity floor)")]
    public async Task TamperedPayload_FailsViaHashChain()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-TAMPER-2", 77m));

        // Mutate the payload (the always-present "memo" string value) but NOT the stored hash → the
        // chain recompute mismatches. Robust to JSON formatting (no reliance on numeric token spacing).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var row = await ctx.Set<NodeAuditEventRow>().FirstAsync(r => r.TenantId == LocalTenantId.Value);
            var tampered = row.Payload.Replace("audit signing test", "TAMPERED-MEMO-PAYLOAD");
            Assert.NotEqual(row.Payload, tampered); // guard: the substitution actually changed something
            ctx.Entry(row).Property(r => r.Payload).CurrentValue = tampered;
            await ctx.SaveChangesAsync();
        }

        var page = await VerifyingReader().ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery());
        var view = Assert.Single(page.Events);
        Assert.Equal(NodeAuditSignatureClassifier.VerificationFailed, view.SignatureState);
    }

    [Fact(DisplayName = "Backward-compat (v0): a NotSigned historical row stays valid (chain-only) — never VerificationFailed, even under the verifying reader")]
    public async Task UnsignedHistoricalRow_StaysNotSigned()
    {
        // The UNSIGNED enlister (no signer) — the pre-PASS-gate / v0 historical write path.
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: new NodeAuditWriteEnlister()));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-V0-1", 10m));

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value).ToListAsync());
        Assert.Null(row.Signature); // v0 — unsigned

        // Even the VERIFYING reader (which would VerificationFail a bad signature) reads an unsigned
        // row as NotSigned — never VerificationFailed (v0 stays valid by the hash chain alone).
        var page = await VerifyingReader().ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery());
        var view = Assert.Single(page.Events);
        Assert.Equal(NodeAuditSignatureClassifier.NotSigned, view.SignatureState);
        Assert.NotEqual(NodeAuditSignatureClassifier.VerificationFailed, view.SignatureState);
    }

    [Fact(DisplayName = "Format gate (v0→v1): a mixed chain of unsigned (v0) then signed (v1) rows classifies each correctly — historical NotSigned, new Verified — one verifiable chain")]
    public async Task MixedV0V1Chain_ClassifiesEachCorrectly()
    {
        // First an unsigned (v0) posting, then two signed (v1) postings on the SAME tenant chain.
        var unsignedStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: new NodeAuditWriteEnlister()));
        await unsignedStore.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-MIX-V0", 10m));

        var signedStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await signedStore.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-MIX-V1-A", 20m));
        await signedStore.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-MIX-V1-B", 30m));

        // The whole chain (v0 + v1 rows) still verifies offline — signing is additive to the chain.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var rows = await ctx.Set<NodeAuditEventRow>()
                .FromSql($"""
                    SELECT * FROM node_audit_events
                    WHERE "TenantId" = {LocalTenantId.Value}
                    ORDER BY rowid ASC
                    """)
                .ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.True(NodeAuditHashChain.Verify(rows), "mixed v0/v1 chain must verify offline");
            // Exactly one unsigned (v0) and two signed (v1).
            Assert.Equal(1, rows.Count(r => r.Signature is null));
            Assert.Equal(2, rows.Count(r => r.Signature is { Length: > 0 }));
        }

        var page = await VerifyingReader().ListAsync(
            LocalTenantId.Value, new NodeAuditEventReaderQuery(PageSize: 50));
        Assert.Equal(3, page.Events.Count);
        // None VerificationFailed; the v0 row is NotSigned, the v1 rows are Verified.
        Assert.All(page.Events, v =>
            Assert.NotEqual(NodeAuditSignatureClassifier.VerificationFailed, v.SignatureState));
        Assert.Single(page.Events, v => v.SignatureState == NodeAuditSignatureClassifier.NotSigned);
        Assert.Equal(2, page.Events.Count(v => v.SignatureState == NodeAuditSignatureClassifier.Verified));
    }

    [Fact(DisplayName = "Atomic: per-event signing rides the SAME enlister transaction — the signed audit row and the JE commit together (no second write)")]
    public async Task Signing_RidesAtomicAdvance_SignedRowCommitsWithJournalEntry()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-ATOMIC-SIGN-1", 100m));

        await using var ctx = await _factory.CreateDbContextAsync();
        // One JE and one SIGNED audit row both landed in the single transaction.
        Assert.Equal(1, await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId));
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value).ToListAsync());
        Assert.NotNull(row.Signature); // signed in the SAME unit-of-work — not a later second write.
    }

    [Fact(DisplayName = "Atomic rollback: when the JE write fails mid-SaveChanges, the already-signed-and-staged audit row rolls back — no orphan signed audit (crash-resume stays atomic)")]
    public async Task Signing_AtomicRollback_NoOrphanSignedAuditRow()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        const string sourceRef = "atomic-signed-dup-key";

        // First posting: signed JE + signed audit row both commit.
        await store.SaveAtomicForTestAsync(
            LocalTenantId, BalancedPostedWithSourceRef("JE-ATOMIC-SIGN-A", 50m, sourceRef));

        // Second posting with the SAME (tenant, SourceReference): the signing enlister SIGNS + stages its
        // row, then SaveChangesAsync throws on the unique index — the business write fails mid-save.
        await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveAtomicForTestAsync(
            LocalTenantId, BalancedPostedWithSourceRef("JE-ATOMIC-SIGN-B", 50m, sourceRef)));

        await using var ctx = await _factory.CreateDbContextAsync();
        // Exactly ONE JE and ONE (signed) audit row survived — the second's signed-and-staged audit row
        // rolled back atomically with its failed JE write. No orphan signed audit for a never-committed JE.
        Assert.Equal(1, await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId));
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value).ToListAsync());
        Assert.NotNull(row.Signature);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JournalEntry BalancedPosted(string id, decimal amount) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "audit signing test",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow))
        {
            Status = JournalEntryStatus.Posted,
        };

    private static JournalEntry BalancedPostedWithSourceRef(string id, decimal amount, string sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "audit signing test (atomic)",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow),
            sourceReference: sourceReference)
        {
            Status = JournalEntryStatus.Posted,
        };
}
