using System.Text;

using Harborline.Api.Foundation.Packs.Serialization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 150 (L1145): "An unknown restricting definition kind in a rule, policy, or retention
/// definition must refuse the write or package installation with a visible reason." The rationale
/// in <c>platform-package-v1.md</c> §4 is about SILENCE — "a weaker floor arrived at silently is
/// worse than a refused install".
///
/// <para>No policy or retention kind exists in <see cref="Harborline.Api.Foundation.Packs.Model.PackContentKind"/>
/// yet, so this is a FORWARD-compatibility gate: a pack authored against a later platform declares a
/// kind this node has never heard of, and this node must refuse the pack rather than ignore the item
/// or silently mis-type it.</para>
/// </summary>
public sealed class PackContentKindAdmissionTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>A pack file carrying one content item, with the <c>kind</c> property supplied verbatim
    /// (or omitted entirely when <paramref name="kindProperty"/> is null).</summary>
    private static byte[] PackFileWith(string? kindProperty) => Utf8($$"""
        {
          "envelope": null,
          "contents": [
            {
              "key": "some.content",
              {{(kindProperty is null ? "" : $"\"kind\": {kindProperty},")}}
              "version": "1.0.0",
              "contentBase64": "e30="
            }
          ],
          "dcp": null
        }
        """);

    [Fact(DisplayName = "ticket 150: a content kind this node does not know refuses the whole pack")]
    public void Unknown_Content_Kind_Refuses_The_Pack()
    {
        // A pack authored against a later platform: a restricting kind that does not exist here.
        var decoded = new PackFileCodec().TryDecode(PackFileWith("\"RetentionPolicy\""));

        Assert.Null(decoded);
    }

    [Fact(DisplayName = "ticket 150: an ABSENT content kind refuses rather than defaulting to FormDefinition")]
    public void Absent_Content_Kind_Refuses_Rather_Than_Defaulting()
    {
        // PackContentKind.FormDefinition is 0, so an omitted "kind" deserializes to it silently.
        // That is the weaker floor arriving quietly: the item installs, typed as something it is not.
        var decoded = new PackFileCodec().TryDecode(PackFileWith(kindProperty: null));

        Assert.Null(decoded);
    }

    [Fact(DisplayName = "ticket 150: a numeric content kind outside the enum refuses")]
    public void Out_Of_Range_Numeric_Content_Kind_Refuses()
    {
        // JsonStringEnumConverter accepts bare numbers, and an undefined number is not a kind.
        var decoded = new PackFileCodec().TryDecode(PackFileWith("9999"));

        Assert.Null(decoded);
    }

    [Fact(DisplayName = "ticket 150 control: a kind this node DOES know still decodes")]
    public void Known_Content_Kind_Still_Decodes()
    {
        var decoded = new PackFileCodec().TryDecode(PackFileWith("\"FormDefinition\""));

        Assert.NotNull(decoded);
        Assert.Equal(
            Harborline.Api.Foundation.Packs.Model.PackContentKind.FormDefinition,
            Assert.Single(decoded!.Contents).Kind);
    }
}
