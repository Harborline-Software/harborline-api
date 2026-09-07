using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// bug-1337 deep-review F1 — <see cref="NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId"/> is the
/// recurring-invoice idempotency key. It MUST be deterministic AND <b>cross-architecture byte-stable</b>:
/// in the planned multi-machine tenant an offline client and the home node derive the SAME
/// <c>SourceReference</c> for the same occurrence on possibly-different CPU architectures (Mac ARM64 +
/// Windows x64). The prior implementation assembled the Guid via the host-endianness <c>BitConverter</c> +
/// the mixed-endian <c>new Guid(int, short, short, …)</c> ctor, which (a) produced a non-conformant version
/// nibble in the rendered string and (b) would differ on a big-endian host. The fix assembles the Guid with
/// an explicit big-endian read; these tests pin determinism and the resulting RFC-4122 v5 shape (the version
/// nibble landing in the right rendered position is the observable proof the byte order is fixed, not
/// host-dependent).
/// </summary>
public sealed class NodeRecurringInvoiceIdDeterminismTests
{
    private static readonly RecurringInvoiceScheduleId ScheduleA =
        new("00000000-0000-0000-0000-0000000000a1");
    private static readonly RecurringInvoiceScheduleId ScheduleB =
        new("00000000-0000-0000-0000-0000000000a2");
    private static readonly DateOnly Occurrence = new(2026, 3, 1);

    [Fact(DisplayName = "F1: same (scheduleId, occurrenceDate) → byte-identical id on every call (determinism)")]
    public void SameInputs_ProduceIdenticalId()
    {
        var first = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence);
        var second = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence);

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact(DisplayName = "F1: distinct inputs → distinct ids (no collision on schedule or date)")]
    public void DistinctInputs_ProduceDistinctIds()
    {
        var a = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence);
        var b = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleB, Occurrence);
        var aNextDay = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence.AddDays(1));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, aNextDay);
    }

    [Fact(DisplayName = "F1: the id is a well-formed RFC-4122 version-5 GUID (proves the big-endian byte order, not host-endianness)")]
    public void Id_IsWellFormedRfc4122Version5Guid()
    {
        var id = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence);

        Assert.True(Guid.TryParse(id.Value, out var guid),
            $"derived id '{id.Value}' must parse as a canonical GUID");

        // The 13th hex character (version nibble) of the canonical 8-4-4-4-12 rendering must be '5'.
        // Under the old BitConverter/mixed-endian assembly this landed elsewhere (rendered '2'/'c'), so a
        // correct '5' here is the observable proof the bytes were assembled in fixed big-endian order.
        var canonical = guid.ToString("D");
        Assert.Equal('5', canonical[14]); // index 14 == first char of the 3rd group ("....-....-Vxxx-....")

        // RFC-4122 variant: the 17th hex character (first nibble of the 4th group) is one of 8/9/a/b.
        Assert.Contains(canonical[19], "89ab");
    }

    [Fact(DisplayName = "F1: id round-trips losslessly through the InvoiceId string column (canonical 36-char GUID)")]
    public void Id_RoundTripsAsCanonicalGuidString()
    {
        var id = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleA, Occurrence);

        Assert.Equal(36, id.Value.Length);
        Assert.Equal(id.Value, new InvoiceId(id.Value).Value);
    }
}
