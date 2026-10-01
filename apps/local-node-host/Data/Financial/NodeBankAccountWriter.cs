using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// The node's admitted coordinator for bank-account aggregate writes. ck-10 S4 (DES-0029, ADR 0038): each write
/// runs the six stages through <see cref="WritePipeline.RunAsync"/>. React adds no audit: the bank-account
/// record is its own evidence (the ck-6 durable-write classification, <c>record-is-audit</c>).
/// </summary>
public sealed class NodeBankAccountWriter(
    IBankAccountMutationRepository accounts,
    AuthorizationGate gate,
    IWritePipelineObserver? pipelineObserver = null)
{
    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    private IBankAccountMutationRepository Accounts => accounts;

    public async ValueTask<BankAccount> CreateAsync(
        BankAccount account,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        // Create binds the account it was given, so it never settles at bind and always returns its result.
        return (await RunAsync(new AccountCreate(this, account, authority), ct).ConfigureAwait(false))!;
    }

    public ValueTask<BankAccount?> ArchiveAsync(
        BankAccountId accountId,
        AuthorizationWriteContext authority,
        CancellationToken ct = default) =>
        RunAsync(new AccountUpdate(this, accountId, authority,
            (account, instant) => account with { ArchivedAt = instant, UpdatedAtUtc = instant }), ct);

    public ValueTask<BankAccount?> SetOpeningBalanceAsync(
        BankAccountId accountId,
        decimal openingBalance,
        Instant cutover,
        AuthorizationWriteContext authority,
        CancellationToken ct = default) =>
        RunAsync(new AccountUpdate(this, accountId, authority,
            (account, instant) => account with
            {
                OpeningBalance = openingBalance,
                CutoverAsOf = cutover,
                UpdatedAtUtc = instant,
            }), ct);

    private ValueTask<BankAccount?> RunAsync<TBound>(
        KernelWrite<TBound, BankAccount, BankAccount, BankAccount> write, CancellationToken ct)
        where TBound : class
        => WritePipeline.RunAsync(write, pipelineObserver, ct);

    private async ValueTask DecideAsync(AuthorizationWriteContext authority, BankAccountId accountId, CancellationToken ct)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", accountId.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
    }

    /// <summary>A new account. Bind refuses an account for another tenant only after the decision, so a
    /// caller the gate refuses learns nothing about the body it sent.</summary>
    private sealed class AccountCreate(NodeBankAccountWriter writer, BankAccount account, AuthorizationWriteContext authority)
        : KernelWrite<BankAccount, BankAccount, BankAccount, BankAccount>
    {
        protected override ValueTask AuthorizeAsync(CancellationToken ct) => writer.DecideAsync(authority, account.Id, ct);

        protected override ValueTask<BankAccount?> BindAsync(CancellationToken ct) =>
            account.TenantId == authority.Tenant
                ? ValueTask.FromResult<BankAccount?>(account)
                : throw new ArgumentException("The bank account tenant does not match the write authority.", nameof(account));

        protected override ValueTask<BankAccount> MutateAsync(BankAccount bound, CancellationToken ct) =>
            ValueTask.FromResult(bound with
            {
                CreatedAtUtc = (Instant)authority.At,
                UpdatedAtUtc = (Instant)authority.At,
            });

        protected override ValueTask<BankAccount> ValidateAsync(BankAccount bound, BankAccount mutation, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(mutation.DisplayName)
                ? throw new ArgumentException("A bank account needs a display name.", nameof(account))
                : ValueTask.FromResult(mutation);

        protected override async ValueTask CommitAsync(BankAccount validated, CancellationToken ct) =>
            await writer.Accounts.AddAsync(validated, ct).ConfigureAwait(false);

        protected override ValueTask<BankAccount> ReactAsync(BankAccount validated, CancellationToken ct) =>
            ValueTask.FromResult(validated);
    }

    /// <summary>A change to an existing account in the authority's tenant. A missing account settles at bind,
    /// so the executor returns null and nothing is written.</summary>
    private sealed class AccountUpdate(
        NodeBankAccountWriter writer,
        BankAccountId accountId,
        AuthorizationWriteContext authority,
        Func<BankAccount, Instant, BankAccount> change)
        : KernelWrite<BankAccount, BankAccount, BankAccount, BankAccount>
    {
        protected override ValueTask AuthorizeAsync(CancellationToken ct) => writer.DecideAsync(authority, accountId, ct);

        protected override async ValueTask<BankAccount?> BindAsync(CancellationToken ct) =>
            await writer.Accounts.GetByIdAsync(authority.Tenant, accountId, ct).ConfigureAwait(false);

        protected override ValueTask<BankAccount> MutateAsync(BankAccount bound, CancellationToken ct) =>
            ValueTask.FromResult(change(bound, (Instant)authority.At));

        /// <summary>Archive and opening balance carry no rule beyond the request's own parsing; bind already
        /// refused an account outside the authority's tenant by not finding it.</summary>
        protected override ValueTask<BankAccount> ValidateAsync(BankAccount bound, BankAccount mutation, CancellationToken ct) =>
            ValueTask.FromResult(mutation);

        protected override async ValueTask CommitAsync(BankAccount validated, CancellationToken ct) =>
            await writer.Accounts.UpdateAsync(validated, ct).ConfigureAwait(false);

        protected override ValueTask<BankAccount> ReactAsync(BankAccount validated, CancellationToken ct) =>
            ValueTask.FromResult(validated);
    }
}
