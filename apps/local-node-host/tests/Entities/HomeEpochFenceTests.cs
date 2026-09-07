using System;
using System.Data.Common;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MD-2 — the G-4 <b>split-brain fence</b>: two devices each believe they are the tenant's home; after a
/// failover bumps the home epoch, ONLY the higher-epoch home can commit, and a superseded (lower-epoch)
/// home's write is REJECTED <b>inside its own transaction</b> — no double-commit, no double-sequence-number
/// (the counter does not advance for the rejected home). The fence rides the SAME
/// <see cref="NodeEfJournalStore.SaveAtomicAsync"/> transaction as the JE write (no TOCTOU) and the SAME
/// numbering-service context as the counter read (closing the Gap-2b sequence-alloc TOCTOU the security
/// verdict named).
/// </summary>
/// <remarks>
/// Builds the REAL production node write stack (the real <see cref="NodeEfJournalStore"/> with the audit +
/// home-epoch-fence enlisters, over an on-disk SQLite <see cref="LocalNodeDbContext"/>) — no mocks. The
/// home epochs are advanced through the REAL roster-signed <see cref="HomeEfHomeEpochStore"/> with a real
/// Ed25519 admin keypair.
/// </remarks>
// Shares the process-global HomeEpochFence.AfterReadHookForTests with HomeEpochFenceAtomicityArchTests; this
// collection serializes the two so xUnit never runs them in parallel and they cannot race on that static hook.
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class HomeEpochFenceTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    private static readonly TenantId Tenant =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
    private static readonly string TenantValue = Tenant.Value;

    private readonly KeyPair _adminA = KeyPair.Generate();
    private readonly KeyPair _adminB = KeyPair.Generate();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-home-epoch-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "fence.db")};Pooling=False";

        var services = new ServiceCollection();
        // The production module set for the JE, invoice, audit, and home-epoch tables.
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        // Belt-and-suspenders: never leak the static interleave hook out of this fixture (the interleaved
        // tests set it transiently inside a try/finally, but clear it here too in case of an early throw).
        HomeEpochFence.AfterReadHookForTests = null;
        _adminA.Dispose();
        _adminB.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private HomeEfHomeEpochStore EpochStore() => new(_factory, new Ed25519Verifier());

    /// <summary>The production JE store wired with the audit enlister + the G-4 home-epoch fence enlister
    /// (exactly how AddNodeFinancialPosting + AddNodeHomeEpochFence compose it).</summary>
    private NodeEfJournalStore FencedStore() =>
        new(_factory,
            NodeJournalWriteAdapters.Create(
                audit: new NodeAuditWriteEnlister(),
                homeEpoch: new HomeEpochFenceEnlister()));

    [Fact]
    public async Task Transaction_Open_Runs_Ef_Connection_Interceptors()
    {
        var interceptor = new CountingConnectionInterceptor();
        var path = Path.Combine(_dir, "interceptor.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<InterceptorProbeDbContext>(options => options
            .UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(interceptor));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<InterceptorProbeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        await HomeEpochFenceTransaction.RunAsync(context, static () => Task.CompletedTask);

        Assert.Equal(1, interceptor.OpenedCount);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    //  SPLIT-BRAIN — only the higher-epoch home commits; the stale home is rejected IN-TRANSACTION.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "MD-2 split-brain: after a failover bumps the home epoch, the STALE home's JE post is REJECTED inside its transaction (no double-commit)")]
    public async Task SplitBrain_StaleHome_JePostRejectedInTransaction_NoDoubleCommit()
    {
        // ── Establish genesis home: device-1 is home at epoch 1. ──
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        // device-1 (still believing it is home at epoch 1) posts its invoice JE — it IS home, so it commits.
        var store = FencedStore();
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-DEVICE1-1001", 100m, "invoice:1001"));
        }
        Assert.Equal(1, await CountJournalEntriesAsync());

        // ── FAILOVER: device-1 is partitioned; recovery-failover promotes device-2 to home at epoch 2. ──
        await epochs.AdvanceAsync(Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));

        // device-1 is STILL running, still believes it is home at epoch 1, and tries to mint invoice #1001
        // again (the split-brain double-commit attempt). The fence reads the current epoch (2) ON THE JE
        // WRITE'S OWN CONTEXT and rejects the stale (epoch-1) assertion BEFORE SaveChangesAsync — the JE +
        // its audit row roll back together. A superseded home literally cannot commit.
        StaleHomeEpochException? rejection = null;
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            rejection = await Assert.ThrowsAsync<StaleHomeEpochException>(() =>
                store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-DEVICE1-DUP-1001", 100m, "invoice:1001-dup")));
        }
        Assert.Equal(1, rejection!.AssertedEpoch);
        Assert.Equal(2, rejection.CurrentEpoch);

        // PROOF (no double-commit): still exactly ONE journal entry. The stale home's second post was
        // rejected inside its transaction — neither the JE nor its audit row persisted.
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(1, await CountAuditRowsAsync());

        // ── device-2 (the new home, epoch 2) posts — it IS home, so it commits. ──
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 2, "device-2")))
        {
            await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-DEVICE2-1002", 200m, "invoice:1002"));
        }
        Assert.Equal(2, await CountJournalEntriesAsync()); // device-1's first + device-2's — never the stale dup
    }

    [Fact(DisplayName = "MD-2: the CURRENT home (equal epoch) commits; only a LOWER epoch is fenced out")]
    public async Task CurrentHome_EqualEpoch_Commits()
    {
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));
        await epochs.AdvanceAsync(Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));

        var store = FencedStore();
        // device-2 asserts epoch 2 == current → commits.
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, 2, "device-2")))
        {
            await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-OK", 10m, "invoice:ok"));
        }
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    [Fact(DisplayName = "MD-2: with NO home epoch established (genesis single-device), the fence is a no-op and the write commits")]
    public async Task NoHomeEpoch_FenceIsNoOp_WriteCommits()
    {
        // No epoch advanced — the genesis single-device state. A scope is active but there is nothing to be
        // stale against, so the fence does not reject (it only ever REJECTS; it never grants/denies a write
        // that has no home epoch to measure against).
        var store = FencedStore();
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-GENESIS", 5m, "invoice:genesis"));
        }
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    [Fact(DisplayName = "MD-2: with NO HomeEpochWriteScope active (every single-device write today), the fenced store behaves exactly as before")]
    public async Task NoScope_FencedStore_BehavesAsBefore()
    {
        // Even with a current home epoch present, a write OUTSIDE a scope is a no-op for the fence — this is
        // the single-device default (no production write opens a scope yet). The write commits unchanged.
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        var store = FencedStore();
        // NO scope entered.
        await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-NOSCOPE", 7m, "invoice:noscope"));
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    //  SEQUENCE-ALLOCATION TOCTOU (Gap-2b) — the stale home cannot even INCREMENT the gap-free counter.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "MD-2 Gap-2b: a STALE home cannot allocate an invoice number — the sequence counter does NOT advance for the rejected home (TOCTOU closed)")]
    public async Task SplitBrain_StaleHome_SequenceAllocationRejected_CounterDoesNotAdvance()
    {
        // ── device-1 is home at epoch 1; it mints the first invoice number. ──
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var chart = new ChartOfAccountsId("CH-1");
        var issueDate = new DateOnly(2026, 6, 16);

        string firstNumber;
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, 1, "device-1")))
        {
            firstNumber = await numbering.NextNumberAsync(chart, issueDate);
        }
        // Persist an invoice under that number so the MAX-scan would see it (the counter "state is the store").
        await SeedInvoiceWithNumberAsync(chart, firstNumber);

        // ── FAILOVER to device-2 at epoch 2 (device-1 is now stale). ──
        await epochs.AdvanceAsync(Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));

        // device-1 (stale, epoch 1) tries to mint ANOTHER number — the Gap-2b attack: even though it never
        // gets to post the JE, the OLD design would let it INCREMENT the counter out-of-band before the JE
        // write. The fence rides the SAME context as the counter scan and rejects it BEFORE any number is
        // computed — the counter does not advance for the rejected home.
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            var ex = await Assert.ThrowsAsync<StaleHomeEpochException>(() =>
                numbering.NextNumberAsync(chart, issueDate));
            Assert.Equal(1, ex.AssertedEpoch);
            Assert.Equal(2, ex.CurrentEpoch);
        }

        // PROOF (counter did not advance): the CURRENT home (device-2, epoch 2) mints next, and gets the
        // number immediately following device-1's FIRST mint — NOT a number that skipped over a stale
        // increment. If the stale home had advanced the counter, device-2 would get …-0003 (a gap); the
        // gap-free invariant holds because the stale mint was rejected before it touched the sequence.
        string nextNumber;
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, 2, "device-2")))
        {
            nextNumber = await numbering.NextNumberAsync(chart, issueDate);
        }
        Assert.EndsWith("-0001", firstNumber);
        Assert.EndsWith("-0002", nextNumber); // exactly the next number — no gap from a stale increment
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    //  INTERLEAVED CONCURRENCY (the G-4 atomicity property) — a promotion races the read-through-write
    //  WINDOW of a stale write. On the BROKEN pre-fix code (fence read in its own autocommit statement)
    //  the promotion commits in the gap AND the stale effect commits = a double-commit. On the FIXED code
    //  (fence read inside the caller's BEGIN IMMEDIATE transaction) the held write lock makes the in-window
    //  promotion fail-fast (SQLITE_BUSY); it is serialized strictly after the stale write — they are
    //  mutually exclusive, so there is no double-commit. These tests interleave via the AfterReadHookForTests
    //  hook (deterministic) rather than relying on wall-clock thread timing.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "MD-2 G-4 atomicity: a promotion injected into the JE fence's read-through-write window CANNOT commit in the gap (the held BEGIN IMMEDIATE write lock serialises it) — no double-commit")]
    public async Task Interleaved_PromotionInJePostFenceWindow_CannotCommitInGap_NoDoubleCommit()
    {
        // ── Genesis: device-1 is home at epoch 1, and has posted one JE. ──
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        var store = FencedStore();
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-1", 100m, "invoice:1"));
        }
        Assert.Equal(1, await CountJournalEntriesAsync());

        // ── The race: device-1 (still asserting epoch 1) starts a JE post. EXACTLY in the window after its
        // fence read and before its save, a recovery-failover promotes device-2 to epoch 2 on a SEPARATE
        // connection. This is the interleaving the G-4 atomicity claim must survive. ──
        var promotionAttemptThrew = false;
        Exception? promotionError = null;
        HomeEpochFence.AfterReadHookForTests = async () =>
        {
            try
            {
                // A real promotion through the production store on its OWN short-lived context/connection.
                await epochs.AdvanceAsync(
                    Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));
            }
            catch (Exception ex)
            {
                // On the FIXED code the held BEGIN IMMEDIATE write lock makes this AdvanceAsync save hit
                // SQLITE_BUSY → it cannot commit epoch 2 in the gap. We capture (not swallow) the failure so
                // the assertions below can prove the promotion was excluded from the window.
                promotionAttemptThrew = true;
                promotionError = ex;
            }
        };

        StaleHomeEpochException? rejection = null;
        Exception? postError = null;
        try
        {
            using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
            {
                try
                {
                    await store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-2-STALE", 100m, "invoice:2"));
                }
                catch (StaleHomeEpochException ex)
                {
                    rejection = ex; // (would only happen if the in-window read already saw epoch 2)
                }
                catch (Exception ex)
                {
                    postError = ex;
                }
            }
        }
        finally
        {
            HomeEpochFence.AfterReadHookForTests = null;
        }

        // ── PROOF OF MUTUAL EXCLUSION (the G-4 property) ──
        // On the FIXED code: the in-window promotion was BLOCKED (SQLITE_BUSY) — it could not commit epoch 2
        // in the gap. The stale write therefore committed legitimately (it WAS home at the instant it held
        // the lock), so the current epoch is still 1 and there are exactly 2 JEs — NO double-home state.
        Assert.True(promotionAttemptThrew,
            "FIXED-code expectation: the promotion injected into the fence's read-through-write window must " +
            "fail-fast (SQLITE_BUSY) because the stale write holds the BEGIN IMMEDIATE write lock. If this " +
            "assertion fails, the promotion committed IN THE GAP — the TOCTOU is open (pre-fix behaviour).");
        Assert.Null(rejection);
        Assert.Null(postError);

        var current = await epochs.GetCurrentEpochAsync(TenantValue);
        Assert.Equal(1, current!.EpochNumber); // the in-window promotion did NOT land

        // No double-commit: the genesis JE + the stale home's (legitimately-home) post = exactly 2; the
        // promotion is serialised AFTER and can be re-applied cleanly now.
        Assert.Equal(2, await CountJournalEntriesAsync());

        // The promotion, retried AFTER the stale transaction released its lock, now succeeds (epoch 2) — and
        // from here a device-1 (epoch-1) write IS rejected by the ordinary sequential fence.
        await epochs.AdvanceAsync(Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));
        Assert.Equal(2, (await epochs.GetCurrentEpochAsync(TenantValue))!.EpochNumber);
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            await Assert.ThrowsAsync<StaleHomeEpochException>(() =>
                store.SaveAtomicForTestAsync(Tenant, BalancedPosted("JE-3-STALE", 100m, "invoice:3")));
        }
        Assert.Equal(2, await CountJournalEntriesAsync()); // still no double-commit
    }

    [Fact(DisplayName = "MD-2 G-4 atomicity (Gap-2b): a promotion injected into the SEQUENCE-ALLOC fence window cannot commit in the gap — the stale home never mints, the counter does not advance")]
    public async Task Interleaved_PromotionInSequenceAllocFenceWindow_CannotCommitInGap_NoDoubleNumber()
    {
        var epochs = EpochStore();
        await epochs.AdvanceAsync(Bump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var chart = new ChartOfAccountsId("CH-1");
        var issueDate = new DateOnly(2026, 6, 16);

        // device-1 mints + persists invoice 0001 while it is home at epoch 1.
        string firstNumber;
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, 1, "device-1")))
        {
            firstNumber = await numbering.NextNumberAsync(chart, issueDate);
        }
        await SeedInvoiceWithNumberAsync(chart, firstNumber);
        Assert.EndsWith("-0001", firstNumber);

        // The race: device-1 (epoch 1) mints again; in the window after the sequence fence read and before
        // the number is computed, device-2 is promoted to epoch 2 on a separate connection.
        var promotionAttemptThrew = false;
        HomeEpochFence.AfterReadHookForTests = async () =>
        {
            try
            {
                await epochs.AdvanceAsync(
                    Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));
            }
            catch
            {
                promotionAttemptThrew = true; // SQLITE_BUSY on the fixed code — promotion blocked in the gap
            }
        };

        string? staleMinted = null;
        try
        {
            using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
            {
                try { staleMinted = await numbering.NextNumberAsync(chart, issueDate); }
                catch (StaleHomeEpochException) { /* in-window read saw epoch 2 — also acceptable, no mint */ }
            }
        }
        finally
        {
            HomeEpochFence.AfterReadHookForTests = null;
        }

        // FIXED-code: the in-window promotion was blocked, so the epoch is still 1 and device-1's mint
        // proceeded (it was legitimately home under the held lock) and produced 0002 — exactly the next
        // number, no gap, no double-number. The promotion is serialised AFTER.
        Assert.True(promotionAttemptThrew,
            "FIXED-code expectation: the promotion injected into the sequence-alloc fence window must " +
            "fail-fast (SQLITE_BUSY); if it committed in the gap the Gap-2b TOCTOU is open (pre-fix).");
        Assert.Equal(1, (await epochs.GetCurrentEpochAsync(TenantValue))!.EpochNumber);
        Assert.Equal("INV-2026-06-16-AA-0002", staleMinted); // the next number — no stale skip

        // And once the promotion lands AFTER (sequentially), device-1 can no longer mint.
        await epochs.AdvanceAsync(Bump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, _adminB));
        using (HomeEpochWriteScope.Enter(new PendingHomeEpochAssertion(TenantValue, AssertedEpoch: 1, "device-1")))
        {
            await Assert.ThrowsAsync<StaleHomeEpochException>(() => numbering.NextNumberAsync(chart, issueDate));
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> CountJournalEntriesAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == Tenant);
    }

    private async Task<int> CountAuditRowsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<NodeAuditEventRow>().CountAsync(r => r.TenantId == TenantValue);
    }

    private async Task SeedInvoiceWithNumberAsync(ChartOfAccountsId chart, string invoiceNumber)
    {
        var invoiceId = new InvoiceId(Guid.NewGuid().ToString("D"));
        var invoice = Invoice.Create(
            tenantId:      Tenant,
            chartId:       chart,
            invoiceNumber: invoiceNumber,
            customerId:    new PartyId("customer-1"),
            issueDate:     new DateOnly(2026, 6, 16),
            dueDate:       new DateOnly(2026, 7, 16),
            lines:         new[]
            {
                InvoiceLine.Create(
                    invoiceId:       invoiceId,
                    lineNumber:      1,
                    description:     "service",
                    quantity:        1m,
                    unitPrice:       100m,
                    incomeAccountId: new GLAccountId("4000")),
            },
            arAccountId:   new GLAccountId("1100"),
            id:            invoiceId,
            createdAtUtc:  new Instant(DateTimeOffset.UtcNow));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<Invoice>().Add(invoice);
        await ctx.SaveChangesAsync();
    }

    private static JournalEntry BalancedPosted(string id, decimal amount, string sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: Tenant,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "home-epoch fence test",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1100"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow),
            sourceReference: sourceReference)
        {
            SourceKind = JournalEntrySource.Invoice,
            Status = JournalEntryStatus.Posted,
        };

    private HomeEpochRecord Bump(
        KeyPair proposer, long epoch, long previous, string home, HomePromotionKind kind,
        KeyPair? coApprover = null)
    {
        var payload = HomeEpochSignaturePayload.For(TenantValue, epoch, previous, home, kind);
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();

        var sig = new Ed25519Signer(proposer).SignAsync(payload, issuedAt, nonce)
            .AsTask().GetAwaiter().GetResult().Signature.ToBase64Url();

        string? coId = null, coSig = null;
        if (coApprover is not null)
        {
            coId = coApprover.PrincipalId.ToBase64Url();
            coSig = new Ed25519Signer(coApprover).SignAsync(payload, issuedAt, nonce)
                .AsTask().GetAwaiter().GetResult().Signature.ToBase64Url();
        }

        return new HomeEpochRecord
        {
            TenantId = TenantValue,
            EpochNumber = epoch,
            PreviousEpochNumber = previous,
            HomeDeviceId = home,
            PromotionKind = kind,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = proposer.PrincipalId.ToBase64Url(),
            Signature = sig,
            CoApproverIssuerId = coId,
            CoApproverSignature = coSig,
        };
    }

    private sealed class InterceptorProbeDbContext(DbContextOptions<InterceptorProbeDbContext> options)
        : DbContext(options);

    private sealed class CountingConnectionInterceptor : DbConnectionInterceptor
    {
        private int _openedCount;
        internal int OpenedCount => Volatile.Read(ref _openedCount);

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openedCount);
            return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }
    }
}
