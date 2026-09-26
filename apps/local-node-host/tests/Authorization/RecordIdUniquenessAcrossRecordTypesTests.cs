using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class RecordIdUniquenessAcrossRecordTypesTests
{
    private const int IdsPerType = 10_000;

    [Fact(DisplayName = "records-eng-28: record ids share one collision-free namespace across record types")]
    public void Records_eng_28_record_ids_are_unique_across_record_types()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        AssertNoDuplicates(ids, nameof(GrantId), () => GrantId.New().ToString());
        AssertNoDuplicates(ids, nameof(FormSubmissionRecordId), () => FormSubmissionRecordId.NewId().Value);
        AssertNoDuplicates(ids, nameof(BankAccountId), () => BankAccountId.NewId().Value);
        AssertNoDuplicates(ids, nameof(JournalEntryId), () => JournalEntryId.NewId().Value);
        AssertNoDuplicates(ids, nameof(InvoiceId), () => InvoiceId.NewId().Value);
        AssertNoDuplicates(ids, nameof(PaymentId), () => PaymentId.NewId().Value);

        var options = new CreateOptions(
            "record", "tenant", "same-nonce", new ActorId("issuer"), new TenantId("tenant"));
        var first = InMemoryEntityStore.DeriveEntityId(new SchemaId("first-record-schema"), options);
        var second = InMemoryEntityStore.DeriveEntityId(new SchemaId("second-record-schema"), options);

        Assert.NotEqual(first.LocalPart, second.LocalPart);
    }

    private static void AssertNoDuplicates(HashSet<string> ids, string recordType, Func<string> mint)
    {
        for (var index = 0; index < IdsPerType; index++)
        {
            var id = mint();
            Assert.True(ids.Add(id), $"{recordType} minted duplicate record id '{id}'.");
        }
    }
}
