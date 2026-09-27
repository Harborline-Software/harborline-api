using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.Calendar.Booking;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackLayoutBookingTests
{
    [Fact(DisplayName = "ticket 737: API pack identities match the pinned Layout and Booking platform identities")]
    public void Api_pack_identities_match_the_pinned_platform_identities()
    {
        Assert.Equal(LayoutPackIdentity.ContentKind, (int)PackContentKind.Layout);
        Assert.Equal(LayoutPackIdentity.Primitive, (int)PackPillar.Layout);
        Assert.Equal(BookingPackIdentity.ResourceContentKind, (int)PackContentKind.Resource);
        Assert.Equal(BookingPackIdentity.BookableContentKind, (int)PackContentKind.Bookable);
        Assert.Equal(BookingPackIdentity.Primitive, (int)PackPillar.Booking);
    }

    [Fact(DisplayName = "ticket 737: Layout and Booking kinds have distinct wire values")]
    public void Pack_kind_and_pillar_values_are_unique()
    {
        var kinds = Enum.GetValues<PackContentKind>().Select(value => (int)value).ToArray();
        var pillars = Enum.GetValues<PackPillar>().Select(value => (int)value).ToArray();

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
        Assert.Equal(pillars.Length, pillars.Distinct().Count());
    }

    [Fact(DisplayName = "ticket 737: Layout and Booking kinds never fall through to Other")]
    public void Layout_and_Booking_kinds_have_explicit_pillars()
    {
        Assert.Equal(PackPillar.Layout, PackPillarMap.ForKind(PackContentKind.Layout));
        Assert.All(
            new[] { PackContentKind.Resource, PackContentKind.Bookable },
            kind => Assert.Equal(PackPillar.Booking, PackPillarMap.ForKind(kind)));
    }

}
