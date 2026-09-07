using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>The node's admitted coordinator for bank-account aggregate writes.</summary>
public sealed class NodeBankAccountWriter(
    IBankAccountMutationRepository accounts,
    AuthorizationGate gate)
{
    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    public async ValueTask<BankAccount> CreateAsync(
        BankAccount account,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (account.TenantId != authority.Tenant)
            throw new ArgumentException("The bank account tenant does not match the write authority.", nameof(account));
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", account.Id.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        await accounts.AddAsync(account with
        {
            CreatedAtUtc = (Instant)authority.At,
            UpdatedAtUtc = (Instant)authority.At,
        }, ct).ConfigureAwait(false);
        return account with
        {
            CreatedAtUtc = (Instant)authority.At,
            UpdatedAtUtc = (Instant)authority.At,
        };
    }

    public async ValueTask<BankAccount?> ArchiveAsync(
        BankAccountId accountId,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", accountId.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        var account = await accounts.GetByIdAsync(authority.Tenant, accountId, ct).ConfigureAwait(false);
        if (account is null) return null;
        var instant = (Instant)authority.At;
        var archived = account with { ArchivedAt = instant, UpdatedAtUtc = instant };
        await accounts.UpdateAsync(archived, ct).ConfigureAwait(false);
        return archived;
    }

    public async ValueTask<BankAccount?> SetOpeningBalanceAsync(
        BankAccountId accountId,
        decimal openingBalance,
        Instant cutover,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", accountId.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        var account = await accounts.GetByIdAsync(authority.Tenant, accountId, ct).ConfigureAwait(false);
        if (account is null) return null;
        var updated = account with
        {
            OpeningBalance = openingBalance,
            CutoverAsOf = cutover,
            UpdatedAtUtc = (Instant)authority.At,
        };
        await accounts.UpdateAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }
}
