using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Drafts;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Drafts;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Drafts;

/// <summary>
/// Pins the durable node D2 submission-draft store (ADR 0135 amendment 2026-07-01): a draft
/// keyed by <c>(tenant, case, party)</c> RELOADS after a faithful restart (a fresh factory
/// over the same file = a fresh "device"), a crypto-shredded subject's draft is hard-deleted
/// and reads as absent, and the retention purge honours legal-hold. Uses a real SQLite file
/// (the SQLCipher interceptor is orthogonal to this resume/shred/hold logic).
/// </summary>
public sealed class NodeSubmissionDraftStoreTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-x");
    private static readonly Guid Party = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"d2-drafts-{Guid.NewGuid():N}.db");

    private static SubmissionDraftProvenance Provenance() => new(
        "cid-abc", "equipment-inspection", "1.0.0",
        SubmissionDraftProvenance.HarborlineJsonLogicV1, new[] { "en-US" });

    private static SubmissionDraft Draft(DraftCaseId caseId, string body, string? subject, DateTimeOffset? expiresAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubmissionDraft(
            new SubmissionDraftKey(Tenant, caseId, Party),
            new FormDefinitionId("equipment-inspection.v1"),
            Provenance(),
            Encoding.UTF8.GetBytes(body),
            subject,
            now, now, expiresAt);
    }

    /// <summary>Opens a fresh DI scope + context factory over the SAME on-disk file — a faithful "restart / other device".</summary>
    private (ServiceProvider Sp, IDbContextFactory<NodeLocalDraftsDbContext> Factory) OpenFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalDraftsDbContext>(opt => opt.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IDbContextFactory<NodeLocalDraftsDbContext>>());
    }

    private static async Task MigrateAsync(IDbContextFactory<NodeLocalDraftsDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.MigrateAsync();
    }

    [Fact(DisplayName = "A persisted draft RELOADS by (tenant, case, party) after a restart / on another device")]
    public async Task Draft_survives_restart_and_resumes_by_tuple()
    {
        var caseId = DraftCaseId.NewId();

        // ── Phase 1: save on "device A". ──
        var (sp1, factory1) = OpenFactory();
        await MigrateAsync(factory1);
        var store1 = new NodeEfSubmissionDraftStore(factory1, new InMemorySubjectErasureRegistry(), new InMemoryLegalHoldRegistry());
        await store1.UpsertAsync(Draft(caseId, "{\"reading\":42}", subject: "subj-1"));
        await sp1.DisposeAsync();

        // ── Faithful restart: the connection string is Pooling=False, so disposing sp1 above already
        //    released the file handle — no global pool clear needed (bug-20260702-8012e553). ──

        // ── Phase 2: reopen over the SAME file (a fresh factory = restart / other device). ──
        var (sp2, factory2) = OpenFactory();
        var store2 = new NodeEfSubmissionDraftStore(factory2, new InMemorySubjectErasureRegistry(), new InMemoryLegalHoldRegistry());

        var resumed = await store2.GetAsync(new SubmissionDraftKey(Tenant, caseId, Party));
        Assert.NotNull(resumed);
        Assert.Equal("{\"reading\":42}", Encoding.UTF8.GetString(resumed!.Body.Span));
        Assert.Equal("subj-1", resumed.SubjectId);
        Assert.Equal("1.0.0", resumed.Provenance.DefinitionVersion);
        Assert.Equal(new[] { "en-US" }, resumed.Provenance.LocaleChain);

        var mine = await store2.ListByPartyAsync(Tenant, Party);
        Assert.Single(mine);
        await sp2.DisposeAsync();
    }

    [Fact(DisplayName = "A crypto-shredded subject's draft is hard-deleted and reads as absent (survives restart)")]
    public async Task Crypto_shred_makes_the_draft_dark()
    {
        var caseId = DraftCaseId.NewId();
        var erasure = new InMemorySubjectErasureRegistry();
        var (sp, factory) = OpenFactory();
        await MigrateAsync(factory);
        var store = new NodeEfSubmissionDraftStore(factory, erasure, new InMemoryLegalHoldRegistry());

        await store.UpsertAsync(Draft(caseId, "{\"pii\":\"x\"}", subject: "subj-erase"));
        // The subject exercises their right to erasure.
        await erasure.MarkErasedAsync(Tenant, new SubjectId("subj-erase"));

        // The draft is now dark: read as absent AND hard-deleted from the store.
        Assert.Null(await store.GetAsync(new SubmissionDraftKey(Tenant, caseId, Party)));
        Assert.Empty(await store.ListByPartyAsync(Tenant, Party));

        // Deletion survives restart (a fresh store over the same file still sees nothing).
        await sp.DisposeAsync();
        var (sp2, factory2) = OpenFactory();
        var store2 = new NodeEfSubmissionDraftStore(factory2, erasure, new InMemoryLegalHoldRegistry());
        Assert.Null(await store2.GetAsync(new SubmissionDraftKey(Tenant, caseId, Party)));
        await sp2.DisposeAsync();
    }

    [Fact(DisplayName = "Retention purge honours legal-hold: an expired held draft is retained; an unheld/subjectless expired draft is purged")]
    public async Task Retention_purge_is_legal_hold_gated()
    {
        var legalHold = new InMemoryLegalHoldRegistry();
        var (sp, factory) = OpenFactory();
        await MigrateAsync(factory);
        var store = new NodeEfSubmissionDraftStore(factory, new InMemorySubjectErasureRegistry(), legalHold);

        var past = DateTimeOffset.UtcNow.AddHours(-1);
        var heldCase = DraftCaseId.NewId();
        var unheldCase = DraftCaseId.NewId();
        var subjectlessCase = DraftCaseId.NewId();

        await store.UpsertAsync(Draft(heldCase, "{}", subject: "subj-held", expiresAt: past));
        await store.UpsertAsync(Draft(unheldCase, "{}", subject: "subj-free", expiresAt: past));
        await store.UpsertAsync(Draft(subjectlessCase, "{}", subject: null, expiresAt: past));

        // A legal hold is placed on one subject — its draft must survive the retention sweep.
        legalHold.Place(Tenant, new SubjectId("subj-held"));

        var purged = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow);

        Assert.Equal(2, purged); // the unheld + the subjectless
        Assert.NotNull(await store.GetAsync(new SubmissionDraftKey(Tenant, heldCase, Party)));   // retained under hold
        Assert.Null(await store.GetAsync(new SubmissionDraftKey(Tenant, unheldCase, Party)));    // purged
        Assert.Null(await store.GetAsync(new SubmissionDraftKey(Tenant, subjectlessCase, Party))); // purged
        await sp.DisposeAsync();
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
