using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Banking;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// The node's admitted coordinator for bank-account aggregate writes. ck-10 S4 (DES-0029, ADR 0038): each write
/// runs the six stages through <see cref="WritePipeline.RunAsync"/>. React adds no audit: the bank-account
/// record is its own evidence (the ck-6 durable-write classification, <c>record-is-audit</c>).
/// </summary>
public sealed class NodeBankAccountWriter(
    IBankAccountMutationRepository accounts,
    AuthorizationGate gate,
    IWritePipelineObserver? pipelineObserver = null,
    NodeEfBankAccountRepository? createKeys = null)
{
    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    private IBankAccountMutationRepository Accounts => accounts;

    private NodeEfBankAccountRepository CreateKeys =>
        createKeys ?? throw new InvalidOperationException("This writer has no create-key store; a keyed create cannot be honoured.");

    public async ValueTask<BankAccount> CreateAsync(
        BankAccount account,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        // Create binds the account it was given, so it never settles at bind and always returns its result.
        return (await RunAsync(new AccountCreate(this, account, authority, null), ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// T-1047: a create under the client's Idempotency-Key, scoped to the authority's tenant and principal. A live
    /// key settles at bind: the same fingerprint replays the account it created, another is refused, and neither
    /// writes. A new key commits with the account in one transaction. A concurrent create of the same key loses
    /// that commit on the key's primary key, rolls back, and binds again to replay the winner's account.
    /// </summary>
    public async ValueTask<BankAccountCreateOutcome> CreateAsync(
        BankAccount account,
        AuthorizationWriteContext authority,
        BankAccountCreateKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(key);
        _ = CreateKeys;
        try
        {
            return await CreateKeyedAsync(account, authority, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
        {
            return await CreateKeyedAsync(account, authority, key, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<BankAccountCreateOutcome> CreateKeyedAsync(
        BankAccount account, AuthorizationWriteContext authority, BankAccountCreateKey key, CancellationToken ct)
    {
        var write = new AccountCreate(this, account, authority, key);
        var created = await RunAsync(write, ct).ConfigureAwait(false);
        if (created is not null)
            return new(BankAccountCreateOutcomeKind.Created, created, null);
        return write.Prior!.Fingerprint == key.Fingerprint
            ? new(BankAccountCreateOutcomeKind.Replayed, null, write.Prior)
            : new(BankAccountCreateOutcomeKind.KeyReused, null, null);
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

    /// <summary>A new account. Bind refuses an account for another tenant, or one that already exists, only after the
    /// decision, so a caller the gate refuses learns nothing about the body it sent.</summary>
    private sealed class AccountCreate(
        NodeBankAccountWriter writer, BankAccount account, AuthorizationWriteContext authority, BankAccountCreateKey? key)
        : KernelWrite<BankAccount, BankAccount, BankAccount, BankAccount>
    {
        /// <summary>The live create key that settled this write at bind, if one did.</summary>
        public BankAccountCreateKeyRow? Prior { get; private set; }

        protected override ValueTask AuthorizeAsync(CancellationToken ct) => writer.DecideAsync(authority, account.Id, ct);

        /// <summary>A create binds a new account: another tenant's account is refused, and so is an id that already
        /// exists, so a repeated create cannot write the account twice.</summary>
        protected override async ValueTask<BankAccount?> BindAsync(CancellationToken ct)
        {
            if (account.TenantId != authority.Tenant)
                throw new ArgumentException("The bank account tenant does not match the write authority.");
            if (key is not null &&
                await writer.CreateKeys.FindCreateKeyAsync(authority.Tenant, authority.Principal.Value, key.Key, ct)
                    .ConfigureAwait(false) is { } prior &&
                prior.ExpiresAt > authority.At)
            {
                Prior = prior;
                return null;
            }
            if (await writer.Accounts.GetByIdAsync(authority.Tenant, account.Id, ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException($"Bank account '{account.Id.Value}' already exists.");
            return account;
        }

        protected override ValueTask<BankAccount> MutateAsync(BankAccount bound, CancellationToken ct) =>
            ValueTask.FromResult(bound with
            {
                CreatedAtUtc = (Instant)authority.At,
                UpdatedAtUtc = (Instant)authority.At,
            });

        protected override ValueTask<BankAccount> ValidateAsync(BankAccount bound, BankAccount mutation, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(mutation.DisplayName)
                ? throw new ArgumentException("A bank account needs a display name.", nameof(mutation))
                : ValueTask.FromResult(mutation);

        protected override async ValueTask CommitAsync(BankAccount validated, CancellationToken ct)
        {
            if (key is null)
            {
                await writer.Accounts.AddAsync(validated, ct).ConfigureAwait(false);
                return;
            }

            // The key record commits in the account's own transaction (T-1047), never beside it.
            await writer.CreateKeys.AddWithCreateKeyAsync(validated, new BankAccountCreateKeyRow
            {
                TenantId = authority.Tenant.Value,
                Principal = authority.Principal.Value,
                Key = key.Key,
                Fingerprint = key.Fingerprint,
                AccountId = validated.Id.Value,
                Response = key.Response(validated),
                CreatedAt = authority.At,
                ExpiresAt = authority.At + BankAccountCreateKeyRow.Retention,
            }, ct).ConfigureAwait(false);
        }

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

/// <summary>T-1047: a client's Idempotency-Key, the SHA-256 fingerprint of its canonical create request, and the
/// route's 201 body for the account it creates, stored with the key so a replay answers with it verbatim.</summary>
public sealed record BankAccountCreateKey(string Key, string Fingerprint, Func<BankAccount, string> Response);

/// <summary>T-1047: what a keyed create did.</summary>
public enum BankAccountCreateOutcomeKind
{
    /// <summary>The account and its key were committed.</summary>
    Created,

    /// <summary>A live key with the same fingerprint: the first request's response, nothing written.</summary>
    Replayed,

    /// <summary>A live key with another fingerprint: refused, nothing written.</summary>
    KeyReused,
}

/// <summary>T-1047: a keyed create's outcome: the created account, or the replayed key with its stored response.</summary>
public sealed record BankAccountCreateOutcome(
    BankAccountCreateOutcomeKind Kind, BankAccount? Account, BankAccountCreateKeyRow? Replayed);
