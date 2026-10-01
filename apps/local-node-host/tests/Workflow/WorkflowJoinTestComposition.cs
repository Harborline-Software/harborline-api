using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

internal static class WorkflowJoinTestComposition
{
    internal static InvoiceApprovalHandler CreateInvoiceHandler() => new(
        NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
        new NodeLiveInvoiceApprovalContext(new NodeAuditWriteEnlister()));

    internal static RecurringGenerationHandler CreateRecurringHandler(IDbContextFactory<LocalNodeDbContext> factory)
    {
        var journals = new NodeEfJournalStore(factory, NodeJournalWriteAdapters.Create());
        var posting = new JournalPostingService(
            new NodeEfAccountResolver(factory),
            new NodeEfPeriodResolver(factory),
            journals,
            TestAuthorization.AllowGate());
        return new RecurringGenerationHandler(new NodeRecurringGenerationContext(new NodeAuditWriteEnlister(), posting));
    }

    internal static async Task SeedFinancialPrerequisitesAsync(
        IDbContextFactory<LocalNodeDbContext> factory,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await context.Set<GLAccount>().AnyAsync(account => account.Id == new GLAccountId("1100"), ct).ConfigureAwait(false))
        {
            var at = new Instant(TestAuthorization.At);
            context.Set<GLAccount>().Add(GLAccount.Create(
                new GLAccountId("1100"), new ChartOfAccountsId("CH-1"), "1100", "Accounts Receivable",
                GLAccountType.Asset, AccountSubtype.AccountsReceivable, "USD", at));
            context.Set<GLAccount>().Add(GLAccount.Create(
                new GLAccountId("4000"), new ChartOfAccountsId("CH-1"), "4000", "Service Income",
                GLAccountType.Revenue, AccountSubtype.OperatingIncome, "USD", at));
            context.Set<FiscalPeriod>().Add(FiscalPeriod.CreateOpen(
                FiscalPeriodId.NewId(), new ChartOfAccountsId("CH-1"), new FiscalYearId("FY-2026"),
                FiscalPeriodKind.Monthly, "2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), at));
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task SeedDraftInvoiceAsync(
        IDbContextFactory<LocalNodeDbContext> factory,
        TenantId tenant,
        string id,
        decimal amount,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var invoiceId = new InvoiceId(id);
        if (await context.Set<Invoice>().AnyAsync(invoice => invoice.TenantId == tenant && invoice.Id == invoiceId, ct).ConfigureAwait(false))
            return;

        var line = InvoiceLine.Create(invoiceId, 1, "Workflow approval fixture", 1m, amount, new GLAccountId("4000"));
        context.Set<Invoice>().Add(Invoice.Create(
            tenant,
            new ChartOfAccountsId("CH-1"),
            id,
            new Harborline.Api.Blocks.People.Foundation.Models.PartyId("customer-1"),
            new DateOnly(2026, 3, 1),
            new DateOnly(2026, 3, 31),
            [line],
            new GLAccountId("1100"),
            new Instant(TestAuthorization.At),
            id: invoiceId));
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<WorkflowDispatchResult> DispatchAsync(
        IWorkflowStore store,
        WorkflowTriggerDispatcher dispatcher,
        WorkflowTrigger trigger,
        CancellationToken ct = default)
    {
        var instance = await store.LoadAsync(trigger.InstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow instance '{trigger.InstanceId}' was not found.");
        var tenant = new TenantId(instance.TenantId);
        var workflowDecision = TestAuthorization.AllowedDecision(tenant, instance.Id);
        var journalId = JournalIdFor(instance, trigger);
        var effectDecision = TestAuthorization.AllowedDecision(
            tenant,
            journalId,
            "journal-entry",
            TeamRolePermissions.LedgerPost);
        return await dispatcher.DispatchAsync(
            trigger,
            new WorkflowDispatchAuthority(workflowDecision, effectDecision),
            ct).ConfigureAwait(false);
    }

    private static string JournalIdFor(WorkflowInstanceRecord instance, WorkflowTrigger trigger)
    {
        if (instance.DefinitionKey == InvoiceApprovalSteps.DefinitionKey)
            return NodeLiveInvoiceApprovalContext.JournalEntryIdFor(
                new WorkflowStepKey(instance.Id, instance.Iteration, InvoiceApprovalSteps.Post)).Value;

        if (instance.DefinitionKey == RecurringGenerationSteps.DefinitionKey
            && trigger.Step.StartsWith(RecurringGenerationSteps.GeneratePrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(
                trigger.Step[RecurringGenerationSteps.GeneratePrefix.Length..],
                "yyyy-MM-dd",
                out var occurrence))
            return NodeRecurringGenerationContext.JournalEntryIdFor(instance, occurrence).Value;

        return NodeLedgerPostingEffect.JournalEntryIdFor(instance).Value;
    }
}
