using System.Text.Json;

using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Contracts.Fields;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>In-process T-733 parity proof for the Layout publication host seam.</summary>
public sealed class LayoutPublishRegistersTests
{
    [Fact(DisplayName = "layout-bound-3: the host supplies released controls for publish and refuses an unregistered control")]
    public void ReleasedAndUnknownFieldControlsGoThroughTheHostPublishSeam()
    {
        var host = Host(recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)]);

        host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new(LayoutPublishRegisters.TextControlId)))), AllowAccess.Instance);

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("signature")))), AllowAccess.Instance),
            LayoutDefinitionCodes.FieldControlUnknown);
    }

    [Fact(DisplayName = "layout-bound-3: omitting the host field-control register fails closed")]
    public void OmittedFieldControlRegisterRefusesTheSameReleasedControl()
    {
        var host = Host(
            recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)],
            options: new(SupplyFieldControls: false));

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new(LayoutPublishRegisters.TextControlId)))), AllowAccess.Instance),
            LayoutDefinitionCodes.FieldControlUnknown);
    }

    [Fact(DisplayName = "layout-bound-7: the host supplies installed-pack pages for publish and refuses an unknown page")]
    public void InstalledPackPagesGoThroughTheHostPublishSeam()
    {
        var host = Host(pageLayouts: [PackLayout], pageMasters: [PackMaster]);

        host.ValidateForPublish(Sealed(PageSurface(new("run", PackLayout.Id, PackMaster.Id, ["body"]))), AllowAccess.Instance);

        AssertRefused(
            () => host.ValidateForPublish(Sealed(PageSurface(new("run", "pack.unknown", PackMaster.Id, ["body"]))), AllowAccess.Instance),
            LayoutDefinitionCodes.PageReferenceUnknown);
    }

    [Fact(DisplayName = "layout-bound-7: an empty host page register fails closed")]
    public void EmptyPageRegisterRefusesTheSameInstalledPackCitation()
    {
        var host = Host();

        AssertRefused(
            () => host.ValidateForPublish(Sealed(PageSurface(new("run", PackLayout.Id, PackMaster.Id, ["body"]))), AllowAccess.Instance),
            LayoutDefinitionCodes.PageReferenceUnknown);
    }

    [Fact(DisplayName = "layout-bound-8: the host supplies released validation rules for publish and refuses an unregistered rule")]
    public void ReleasedAndUnknownValidationRulesGoThroughTheHostPublishSeam()
    {
        var host = Host(recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)]);

        host.ValidateForPublish(Sealed(CaptureSurface(new(false, [LayoutPublishRegisters.RequiredValueRuleId]))), AllowAccess.Instance);

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, ["rules.unknown"]))), AllowAccess.Instance),
            LayoutDefinitionCodes.ValidationRuleUnknown);
    }

    [Fact(DisplayName = "layout-bound-8: omitting the host validation-rule register fails closed")]
    public void OmittedValidationRuleRegisterRefusesTheSameReleasedRule()
    {
        var host = Host(
            recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)],
            options: new(SupplyValidationRules: false));

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [LayoutPublishRegisters.RequiredValueRuleId]))), AllowAccess.Instance),
            LayoutDefinitionCodes.ValidationRuleUnknown);
    }

    private static readonly LayoutPageLayoutDefinition PackLayout = new(
        "pack.a4", "a4", LayoutPageOrientation.Portrait,
        new("12mm", "12mm", "12mm", "12mm"), new("10mm", "10mm"));

    private static readonly LayoutPageMasterDefinition PackMaster = new(
        "pack.master", PackLayout.Id,
        new(null, "first.center", null),
        new(null, "left.center", null),
        new(null, "right.center", null));

    private static LayoutPublishRegisters Host(
        IReadOnlyList<LayoutRecordFieldDescriptor>? recordFields = null,
        IReadOnlyList<LayoutPageLayoutDefinition>? pageLayouts = null,
        IReadOnlyList<LayoutPageMasterDefinition>? pageMasters = null,
        LayoutPublishRegisterOptions? options = null)
        => new(new LayoutPublishState(recordFields, pageLayouts, pageMasters), options);

    private static void AssertRefused(Action publish, string code)
    {
        var refused = Assert.Throws<DefinitionRefusalException>(publish);
        Assert.Contains(refused.Refusals, refusal => refusal.Code == code);
    }

    private sealed class AllowAccess : ILayoutAccess
    {
        public static AllowAccess Instance { get; } = new();
        public bool CanAuthor() => true;
        public bool CanPublish() => true;
        public bool CanRead(LayoutBinding binding) => true;
        public bool CanOpen(string surfaceId) => true;
    }

    private static LayoutDefinition Sealed(LayoutDefinition definition)
        => definition with { Envelope = definition.Envelope with { Requires = [new(LayoutPackIdentity.Capability, "1.0.0")] } };

    private static LayoutDefinition CaptureSurface(LayoutCaptureProperties capture) => new(
        new("surface.invoice", "1.0.0", "tenant-733", LayoutCascadeLayer.DomainPackage,
            JsonSerializer.SerializeToElement(new { source = "t-733" }), "standard", false, [], new DefinitionContractVersion(1, 0)),
        1, LayoutMedium.Screen, LayoutIntent.Capture,
        [new("reference", "layout.field", new LayoutRecordFieldBinding("invoice.reference"), [], Capture: capture)],
        [], [], [], null, []);

    private static LayoutDefinition PageSurface(LayoutPageRun run) => new(
        new("surface.statement", "1.0.0", "tenant-733", LayoutCascadeLayer.DomainPackage,
            JsonSerializer.SerializeToElement(new { source = "t-733" }), "standard", false, [], new DefinitionContractVersion(1, 0)),
        1, LayoutMedium.Page, LayoutIntent.Observe,
        [
            new("heading", "layout.text", new LayoutStaticBinding(JsonSerializer.SerializeToElement("Statement")), [],
                FlowRole: LayoutFlowRole.Static, StaticRegion: "first.center"),
            new("body", "layout.text", new LayoutStaticBinding(JsonSerializer.SerializeToElement("Body")), []),
        ],
        [], [], [run], null, []);
}
