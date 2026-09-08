using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

internal static class JournalStoreTestExtensions
{
    internal static Task SaveAtomicForTestAsync(
        this IJournalStore store,
        TenantId tenant,
        JournalEntry entry,
        CancellationToken ct = default)
        => store.SaveAtomicForTestAsync(
            tenant,
            entry,
            TestAuthorization.Write(tenant),
            ct);

    internal static Task SaveAtomicForTestAsync(
        this IJournalStore store,
        TenantId tenant,
        JournalEntry entry,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (authority.Tenant != tenant)
        {
            throw new ArgumentException(
                $"Write authority tenant '{authority.Tenant.Value}' does not match '{tenant.Value}'.",
                nameof(authority));
        }

        var decision = TestAuthorization.AllowedDecision(
            tenant,
            entry.Id.Value,
            recordKind: "journal-entry",
            operation: TeamRolePermissions.LedgerPost,
            principal: authority.Principal.Value,
            at: authority.At);
        return store.SaveAtomicAsync(tenant, entry, decision, ct);
    }
}
