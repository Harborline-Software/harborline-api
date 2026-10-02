using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ck-10 S4 (DES-0029, ADR 0038): bank-account create, archive and opening balance run the six stages through
/// <see cref="WritePipeline.RunAsync"/>. Each refusal stops the write at its own stage and writes nothing.
/// </summary>
public sealed class BankAccountWritePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Before = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("bank-pipeline");
    private static readonly TenantId OtherTenant = new("bank-pipeline-other");
    private static readonly AuthorizationWriteContext Authority = new(new ActorId("bank-operator"), Tenant, At);

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S4: create runs the six stages in order and stores the account stamped at the admitted instant")]
    public async Task Create_RunsTheSixStages_AndStampsTheAdmittedInstant()
    {
        var h = new Harness();

        var created = await h.Writer.CreateAsync(Account("acct-1", Tenant, "Ops Checking"), Authority);

        Assert.Equal(SixStages, h.Stages);
        var stored = Assert.Single(h.Accounts.Rows.Values);
        Assert.Equal("Ops Checking", stored.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), (DateTimeOffset)stored.CreatedAtUtc);
        Assert.Equal(stored, created);
    }

    [Fact(DisplayName = "ck-10 S4: a refused caller stops at authorize, before bind can judge another tenant's account")]
    public async Task Create_RefusedCaller_StopsAtAuthorize_BeforeTheTenantBind()
    {
        var h = new Harness(allow: false);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await h.Writer.CreateAsync(Account("acct-1", OtherTenant, "Ops Checking"), Authority));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.Empty(h.Accounts.Rows);
    }

    [Fact(DisplayName = "ck-10 S4: an allowed create for another tenant's account is refused at bind and stores nothing")]
    public async Task Create_AnotherTenantsAccount_IsRefusedAtBind()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.Writer.CreateAsync(Account("acct-1", OtherTenant, "Ops Checking"), Authority));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
        Assert.Empty(h.Accounts.Rows);
    }

    [Fact(DisplayName = "ck-10 S4: a blank display name is refused at validate, stores nothing, and a named retry stores the account")]
    public async Task Create_BlankDisplayName_IsRefusedAtValidate_AndARetrySucceeds()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.Writer.CreateAsync(Account("acct-1", Tenant, "  "), Authority));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Empty(h.Accounts.Rows);

        h.Stages.Clear();
        await h.Writer.CreateAsync(Account("acct-1", Tenant, "Ops Checking"), Authority);
        Assert.Equal(SixStages, h.Stages);
        Assert.Equal("Ops Checking", h.Accounts.Rows[new BankAccountId("acct-1")].DisplayName);
    }

    [Fact(DisplayName = "ck-10 S4: archive runs the six stages and stamps the archive at the admitted instant")]
    public async Task Archive_RunsTheSixStages()
    {
        var h = new Harness();
        h.Accounts.Seed(Account("acct-1", Tenant, "Ops Checking"));

        var archived = await h.Writer.ArchiveAsync(new BankAccountId("acct-1"), Authority);

        Assert.Equal(SixStages, h.Stages);
        var stored = h.Accounts.Rows[new BankAccountId("acct-1")];
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), (DateTimeOffset)stored.ArchivedAt!.Value);
        Assert.Equal(stored, archived);
    }

    [Fact(DisplayName = "ck-10 S4: a refused archive stops at authorize; the account is neither read nor changed")]
    public async Task Archive_RefusedCaller_StopsAtAuthorize()
    {
        var h = new Harness(allow: false);
        var seeded = Account("acct-1", Tenant, "Ops Checking");
        h.Accounts.Seed(seeded);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await h.Writer.ArchiveAsync(new BankAccountId("acct-1"), Authority));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.Equal(0, h.Accounts.Reads);
        Assert.Equal(seeded, h.Accounts.Rows[new BankAccountId("acct-1")]);
    }

    [Fact(DisplayName = "ck-10 S4: an account outside the authority's tenant settles at bind: null, and nothing is written")]
    public async Task Archive_AnotherTenantsAccount_SettlesAtBind()
    {
        var h = new Harness();
        var foreign = Account("acct-1", OtherTenant, "Ops Checking");
        h.Accounts.Seed(foreign);

        Assert.Null(await h.Writer.ArchiveAsync(new BankAccountId("acct-1"), Authority));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
        Assert.Equal(foreign, h.Accounts.Rows[new BankAccountId("acct-1")]);
        Assert.Equal(0, h.Accounts.Writes);
    }

    [Fact(DisplayName = "ck-10 S4: opening balance runs the six stages and stores the balance and cutover it was given")]
    public async Task SetOpeningBalance_RunsTheSixStages()
    {
        var h = new Harness();
        h.Accounts.Seed(Account("acct-1", Tenant, "Ops Checking"));
        var cutover = (Instant)new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await h.Writer.SetOpeningBalanceAsync(new BankAccountId("acct-1"), 1250.75m, cutover, Authority);

        Assert.Equal(SixStages, h.Stages);
        var stored = h.Accounts.Rows[new BankAccountId("acct-1")];
        Assert.Equal(1250.75m, stored.OpeningBalance);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), (DateTimeOffset)stored.CutoverAsOf);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), (DateTimeOffset)stored.UpdatedAtUtc);
    }

    [Fact(DisplayName = "ck-10 S4: a request cancelled after the create commits still returns the committed account, which stays usable")]
    public async Task Create_CancelledAfterCommit_ReturnsTheCommittedAccount_WhichStaysUsable()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Accounts.AfterWrite = cancellation.Cancel;

        var created = await h.Writer.CreateAsync(Account("acct-1", Tenant, "Ops Checking"), Authority, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(SixStages, h.Stages);
        Assert.Equal(created, h.Accounts.Rows[new BankAccountId("acct-1")]);

        h.Stages.Clear();
        h.Accounts.AfterWrite = null;
        var archived = await h.Writer.ArchiveAsync(new BankAccountId("acct-1"), Authority);
        Assert.Equal(SixStages, h.Stages);
        Assert.Single(h.Accounts.Rows);
        Assert.NotNull(archived!.ArchivedAt);
    }

    [Fact(DisplayName = "ck-10 S4: retrying the same create after a cancelled request is refused at bind and leaves exactly one account")]
    public async Task Create_RetriedAfterACancelledCommit_IsRefusedAtBind_AndLeavesOneAccount()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Accounts.AfterWrite = cancellation.Cancel;
        var original = Account("acct-1", Tenant, "Ops Checking");
        var created = await h.Writer.CreateAsync(original, Authority, cancellation.Token);

        h.Stages.Clear();
        h.Accounts.AfterWrite = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Writer.CreateAsync(original, Authority));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
        Assert.Equal(1, h.Accounts.Writes);
        Assert.Equal(created, Assert.Single(h.Accounts.Rows.Values));
    }

    [Fact(DisplayName = "ck-10 S4: a create without an account is refused before any stage runs")]
    public async Task Create_NullAccount_IsRefusedBeforeAnyStage()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await h.Writer.CreateAsync(null!, Authority));

        Assert.Empty(h.Stages);
        Assert.Empty(h.Accounts.Rows);
    }

    private static BankAccount Account(string id, TenantId tenant, string displayName) => new(
        new BankAccountId(id), tenant, default, default, displayName, null, default, default,
        0m, (Instant)Before, null, (Instant)Before, (Instant)Before);

    private sealed class Harness : IWritePipelineObserver
    {
        public Harness(bool allow = true) =>
            Writer = new NodeBankAccountWriter(Accounts, TestAuthorization.Gate(allow), this);

        public AccountStore Accounts { get; } = new();
        public NodeBankAccountWriter Writer { get; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);
    }

    /// <summary>An in-memory account store whose tenant-scoped read mirrors the durable repository's.</summary>
    private sealed class AccountStore : IBankAccountMutationRepository
    {
        public Dictionary<BankAccountId, BankAccount> Rows { get; } = [];
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Action? AfterWrite { get; set; }

        public void Seed(BankAccount account) => Rows[account.Id] = account;

        public Task<BankAccount?> GetByIdAsync(TenantId tenantId, BankAccountId id, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Rows.TryGetValue(id, out var row) && row.TenantId == tenantId ? row : null);
        }

        public Task<IReadOnlyList<BankAccount>> ListAsync(TenantId tenantId, bool includeArchived = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BankAccount>>(Rows.Values.Where(row => row.TenantId == tenantId).ToList());

        public Task AddAsync(BankAccount account, CancellationToken ct = default)
        {
            Writes++;
            Rows.Add(account.Id, account);
            AfterWrite?.Invoke();
            return Task.CompletedTask;
        }

        public Task UpdateAsync(BankAccount account, CancellationToken ct = default)
        {
            Writes++;
            Rows[account.Id] = account;
            return Task.CompletedTask;
        }
    }
}
