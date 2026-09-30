using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-909 ck-1 mutation evidence: the admission checks in <c>PackAdmissionClassificationChecks.cs</c> called
/// directly, for the cases the pack-install route tests do not reach (a compiled key on another kind, and a
/// null candidate list).
/// </summary>
public sealed class PackCompiledShapeCheckTests
{
    private static PackContentItem Item(string key, PackContentKind kind) =>
        new(key, kind, "1.0.0", ReadOnlyMemory<byte>.Empty, Cid.FromBytes([]));

    [Fact(DisplayName = "T-909 ck-1: only a record type can claim a compiled shape; another kind with a compiled key is not refused")]
    [Trait("Holds", "kernel-core-ck-1")]
    public void Only_a_record_type_with_a_compiled_key_is_a_compiled_shape_claim()
    {
        var refusals = PackCompiledShapeCheck.FindRefusals(
        [
            Item("Record-Type", PackContentKind.FormDefinition),
            Item("intake", PackContentKind.RecordType),
            Item("Record-Type", PackContentKind.RecordType),
        ]);

        var refusal = Assert.Single(refusals);
        Assert.Equal(PackInstallCodes.RefusedCompiledShapeReplacement, refusal.Code);
        Assert.Equal("/contents/2/contentBase64", refusal.Pointer);
    }

    [Fact(DisplayName = "T-909 ck-1: the compiled-shape check names its own argument when the candidate list is null")]
    [Trait("Holds", "kernel-core-ck-1")]
    public void Compiled_shape_check_refuses_a_null_candidate_list_by_name()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => PackCompiledShapeCheck.FindRefusals(null!));
        Assert.Equal("contents", thrown.ParamName);
    }

    [Fact]
    public void Transport_rule_check_refuses_a_null_candidate_list_by_name()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => PackTransportRuleCheck.FindRefusals(null!));
        Assert.Equal("contents", thrown.ParamName);
    }

    [Fact]
    public void Destination_classifier_refuses_a_null_candidate_list_by_name()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => PackDestinationClassifier.Classify(null!));
        Assert.Equal("contents", thrown.ParamName);
    }
}
