using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using PeopleModels = Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// PM-pack service for the ADR 0120 lease→sub-ledger mapping (PR-D).
///
/// <para>
/// <b>Responsibilities:</b>
/// <list type="number">
///   <item>On lease activation: mint (or resolve) one <see cref="SubLedgerAccount"/>
///     (Kind=Receivable, ControlAccountId=the AR control, PartyId=primary tenant)
///     and record a <see cref="LeaseSubLedgerLink"/>.</item>
///   <item>Expose the offline read path:
///     <c>lease → <see cref="GetSubLedgerPositionAsync"/> → ISubLedgerReadModel</c>
///     served by the local node, fully offline.</item>
///   <item>Expose the link so the PM write-path can stamp
///     <c>Invoice.SubLedgerAccountId</c> at invoice-creation time.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pack-boundary discipline (ADR 0111 D3):</b>
/// <list type="bullet">
///   <item>This service lives in the PM pack (<c>blocks-leases</c>) — NOT in any
///     financial block.</item>
///   <item>It references ONLY the LOW identity assembly
///     (<c>blocks-financial-subledger</c>) for <see cref="SubLedgerAccount"/> /
///     <see cref="SubLedgerAccountId"/> / <see cref="ISubLedgerReadModel"/> /
///     <see cref="ISubLedgerAccountRepository"/>. It does NOT reference AR, AP,
///     payments, or the HIGH projection assembly.</item>
///   <item>The D3 arch-test will fail the build if any financial cluster type leaks
///     into this assembly (enforced at compile time).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Kind-guard (ADR 0120 PR-D binding carry #2):</b> account creation uses
/// <see cref="SubLedgerAccount.CreateWithKindGuard"/> with
/// <see cref="GLAccountType.Asset"/> (AR control is always Asset-class). Any attempt
/// to create a Payable account under an AR control throws
/// <see cref="ArgumentException"/> at the creation seam — mis-binding is impossible.
/// </para>
///
/// <para>
/// <b>Idempotency:</b> <see cref="ActivateAsync"/> is safe to call multiple times for
/// the same lease. If a <see cref="LeaseSubLedgerLink"/> already exists, the
/// pre-existing <see cref="SubLedgerAccountId"/> is returned without creating a second
/// account. The <see cref="SubLedgerAccount"/> is minted via <c>ExternalRef</c> for
/// idempotent mint-or-get.
/// </para>
///
/// <para>
/// <b>C-FIN-3 — security deposit:</b> by default, a security deposit should be
/// recorded as an unapplied AR credit on this account (a <c>Payment</c> with
/// <c>PaymentDirection.Inbound</c> and <c>UnappliedAmount > 0</c>). The
/// <em>jurisdiction-segregated deposit-liability path</em> — where some jurisdictions
/// (e.g. California, UK) require deposits to be held in a segregated trust account and
/// exposed as a liability rather than netted against AR — is a LATER pack feature
/// decision. Do NOT silently net deposits in jurisdictions where segregation is required.
/// This comment is the implementation decision record; the segregated path is deferred.
/// </para>
/// </summary>
public sealed class LeaseSubLedgerService
{
    private readonly ILeaseSubLedgerLinkRepository _links;
    private readonly ISubLedgerAccountRepository _accounts;
    private readonly ISubLedgerReadModel _readModel;

    public LeaseSubLedgerService(
        ILeaseSubLedgerLinkRepository links,
        ISubLedgerAccountRepository accounts,
        ISubLedgerReadModel readModel)
    {
        _links    = links    ?? throw new ArgumentNullException(nameof(links));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
    }

    /// <summary>
    /// Activate the sub-ledger for <paramref name="leaseId"/>: mint (or resolve) one
    /// <see cref="SubLedgerAccount"/> and record a <see cref="LeaseSubLedgerLink"/>.
    ///
    /// <para>
    /// The account is keyed by a stable derivation <c>ExternalRef</c>:
    /// <c>pm:lease:v1:{leaseId}</c>. On the first call for a given lease this
    /// mints a new account. On subsequent calls (idempotent) the existing account id
    /// is returned without creating a second account.
    /// </para>
    ///
    /// <para>
    /// <b>Kind-guard:</b> <see cref="SubLedgerAccount.CreateWithKindGuard"/> is called
    /// with <paramref name="arControlAccountId"/> type = Asset. An incompatible kind
    /// (Payable) throws immediately at this seam.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="chartId">The chart of accounts the AR control belongs to.</param>
    /// <param name="leaseId">The lease being activated.</param>
    /// <param name="arControlAccountId">
    /// The AR control GL account. MUST be an Asset-class account (enforced by the Kind-guard).
    /// </param>
    /// <param name="primaryTenantPartyId">
    /// The primary tenant's <c>PartyId</c> (from <see cref="Lease.Tenants"/>).
    /// </param>
    /// <param name="now">Clock instant for <c>CreatedAtUtc</c>.</param>
    /// <param name="cancellationToken"/>
    /// <returns>
    /// The <see cref="SubLedgerAccountId"/> of the (newly minted or pre-existing) account.
    /// </returns>
    public async Task<SubLedgerAccountId> ActivateAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        LeaseId leaseId,
        GLAccountId arControlAccountId,
        PeopleModels.PartyId primaryTenantPartyId,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        // Idempotency: if a link already exists, return its sub-ledger account id.
        var existing = await _links.GetByLeaseAsync(tenantId, leaseId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing.SubLedgerAccountId;

        // Mint the sub-ledger account with Kind-guard enforced.
        // AR control accounts are Asset-class — mis-binding (e.g. Payable→Asset) throws here.
        var externalRef = $"pm:lease:v1:{leaseId.Value}";
        var existingAccount = await _accounts.GetByExternalRefAsync(
            tenantId, chartId, externalRef, cancellationToken).ConfigureAwait(false);

        SubLedgerAccount account;
        if (existingAccount is not null)
        {
            account = existingAccount;
        }
        else
        {
            // CreateWithKindGuard enforces Receivable→Asset consistency.
            // If the caller passes an AP (Liability) control account, this throws ArgumentException
            // immediately — mis-binding is impossible at this creation seam (PR-D binding carry #2).
            account = SubLedgerAccount.CreateWithKindGuard(
                tenantId: tenantId,
                chartId: chartId,
                controlAccountId: arControlAccountId,
                controlAccountType: GLAccountType.Asset, // AR control = Asset
                kind: SubLedgerKind.Receivable,
                partyId: primaryTenantPartyId,
                now: now,
                reference: $"AR — Lease {leaseId.Value}",
                externalRef: externalRef);

            await _accounts.UpsertAsync(tenantId, account, cancellationToken).ConfigureAwait(false);
        }

        // Record the PM-pack link.
        var link = new LeaseSubLedgerLink
        {
            TenantId           = tenantId,
            LeaseId            = leaseId,
            SubLedgerAccountId = account.Id,
            CreatedAtUtc       = now,
        };
        await _links.UpsertAsync(tenantId, link, cancellationToken).ConfigureAwait(false);

        return account.Id;
    }

    /// <summary>
    /// Look up the <see cref="SubLedgerAccountId"/> for <paramref name="leaseId"/>.
    /// Returns null if the lease has not yet been activated.
    /// </summary>
    public async Task<SubLedgerAccountId?> GetSubLedgerAccountIdAsync(
        TenantId tenantId,
        LeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        var link = await _links.GetByLeaseAsync(tenantId, leaseId, cancellationToken)
            .ConfigureAwait(false);
        return link?.SubLedgerAccountId;
    }

    /// <summary>
    /// Offline read path (ADR 0115 + ADR 0120 §PR-D rationale):
    /// <c>lease → LeaseSubLedgerLink → SubLedgerAccountId → ISubLedgerReadModel.GetPositionAsync</c>.
    ///
    /// Serves the per-lease balance without the PartyId-overmatch (the overmatch that prompted ADR 0120).
    /// Returns null when the lease has no sub-ledger link (not yet activated).
    /// </summary>
    public async Task<SubLedgerPosition?> GetSubLedgerPositionAsync(
        TenantId tenantId,
        LeaseId leaseId,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        var accountId = await GetSubLedgerAccountIdAsync(tenantId, leaseId, cancellationToken)
            .ConfigureAwait(false);
        if (accountId is null)
            return null;

        return await _readModel.GetPositionAsync(tenantId, accountId.Value, asOf, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Offline read path — aging:
    /// <c>lease → LeaseSubLedgerLink → SubLedgerAccountId → ISubLedgerReadModel.GetAgingForSubLedgerAsync</c>.
    /// Returns null when the lease has no sub-ledger link.
    /// </summary>
    public async Task<AgingSummary?> GetSubLedgerAgingAsync(
        TenantId tenantId,
        LeaseId leaseId,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        var accountId = await GetSubLedgerAccountIdAsync(tenantId, leaseId, cancellationToken)
            .ConfigureAwait(false);
        if (accountId is null)
            return null;

        return await _readModel.GetAgingForSubLedgerAsync(tenantId, accountId.Value, asOf, cancellationToken)
            .ConfigureAwait(false);
    }
}
