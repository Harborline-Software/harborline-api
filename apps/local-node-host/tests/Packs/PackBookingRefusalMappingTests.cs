using System.Text;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-1034 follow-up to api #502: a platform Booking refusal that does not point under
/// <c>/entries/{index}</c> stays a pack-level refusal with the platform's pointer and target.
/// The pinned platform (22491171, <c>BookingDefinitionPackage.cs:64</c>) only emits entry pointers,
/// so no route can produce one; the mapping is driven directly.
/// </summary>
public sealed class PackBookingRefusalMappingTests
{
    private static readonly PackContentItem[] Items =
    [
        new("acme.surface", PackContentKind.Bookable, "1.0.0", Encoding.UTF8.GetBytes("{}"), Cid.FromBytes([])),
    ];

    [Theory(DisplayName = "T-1034 follow-up: a Booking refusal outside an entry is a pack-level refusal")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/entries")]
    [InlineData("/entries/")]
    [InlineData("/entries/x/requires/0")]
    [InlineData("/entries/1")]
    [InlineData("/entries/+0")]
    [InlineData("/entries/-1")]
    [InlineData("/envelope/contract")]
    public void Pointer_outside_an_entry_keeps_the_platform_refusal(string pointer)
    {
        var place = PackInstaller.BookingRefusalPlace(
            new DefinitionRefusal("booking.pack.content_unsupported", pointer, "platform.target"), Items);

        Assert.Equal((pointer, "platform.target"), place);
    }

    [Theory(DisplayName = "T-1034 follow-up: a Booking refusal under an entry names that definition")]
    [InlineData("/entries/0", "")]
    [InlineData("/entries/0/requires/0", "/requires/0")]
    public void Pointer_under_an_entry_names_the_definition(string pointer, string expected)
    {
        var place = PackInstaller.BookingRefusalPlace(
            new DefinitionRefusal("booking.bookable.required_resource_unknown", pointer), Items);

        Assert.Equal((expected, "acme.surface@1.0.0"), place);
    }
}
