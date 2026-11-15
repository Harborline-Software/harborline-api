using System.Text;
using System.Text.Json;

using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Contracts.Authorization;
using Harborline.Contracts.Fields;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>In-process T-733 parity proof for the Layout host seam at publication and at render.</summary>
public sealed class LayoutPublishRegistersTests
{
    [Fact(DisplayName = "layout-bound-3: the host supplies the released field-control register instance")]
    public void BuildSuppliesTheReleasedFieldControlRegister()
    {
        Assert.Same(LayoutFieldControlRegistry.Released, Host().Build().FieldControls);
    }

    [Fact(DisplayName = "layout-bound-8: the host supplies the released validation-rule register instance")]
    public void BuildSuppliesTheReleasedValidationRuleRegister()
    {
        Assert.Same(LayoutValidationRuleRegistry.Released, Host().Build().ValidationRules);
    }

    [Theory(DisplayName = "layout-bound-8: publish admission receives the selected validation-rule register")]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishAdmissionReceivesTheSelectedValidationRuleRegister(bool supplyValidationRules)
    {
        LayoutHostRegisters? received = null;
        var host = new LayoutPublishRegisters(
            new(), new(SupplyValidationRules: supplyValidationRules),
            (definition, registers, author) =>
            {
                received = registers;
                LayoutDefinitionAdmission.ValidateForPublish(definition, registers, author);
            });

        host.ValidateForPublish(Sealed(CaptureSurface(new(true, []))), AllowAccess.Instance);

        Assert.NotNull(received);
        // T-1013 Claim scope: publish receives the released instance under its supply option.
        if (supplyValidationRules)
            Assert.Same(LayoutValidationRuleRegistry.Released, received.ValidationRules);
        else
            Assert.Null(received.ValidationRules);
    }

    [Fact(DisplayName = "layout-bound-3: the host supplies released controls for publish and refuses an unregistered control")]
    public void ReleasedAndUnknownFieldControlsGoThroughTheHostPublishSeam()
    {
        var host = Host(recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)]);

        host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("text")))), AllowAccess.Instance);

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("signature")))), AllowAccess.Instance),
            LayoutDefinitionCodes.FieldControlUnknown);
    }

    [Fact(DisplayName = "layout-bound-3: the host publishes currency with decimals and refuses precision")]
    public void ReleasedCurrencyParametersGoThroughTheHostPublishSeam()
    {
        var host = Host(recordFields: [new("invoice.reference", FieldScalarValueShape.Number, HasValueDomain: false)]);

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("currency", JsonSerializer.Deserialize<JsonElement>("{\"precision\":2}"))))), AllowAccess.Instance),
            "layout.capture.control_parameters_invalid", "/blocks/0/capture/control/parameters");

        host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("currency", JsonSerializer.Deserialize<JsonElement>("{\"decimals\":2}"))))), AllowAccess.Instance);
    }

    [Fact(DisplayName = "layout-bound-3: omitting the host field-control register fails closed")]
    public void OmittedFieldControlRegisterRefusesTheSameReleasedControl()
    {
        var host = Host(
            recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)],
            options: new(SupplyFieldControls: false));

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, [], Control: new("text")))), AllowAccess.Instance),
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

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, ["rules.value-required"]))), AllowAccess.Instance),
            "layout.capture.validation_rule_unknown", "/blocks/0/capture/validation_rules/0");

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, ["rules.unknown"]))), AllowAccess.Instance),
            "layout.capture.validation_rule_unknown");
    }

    [Fact(DisplayName = "layout-bound-8: the host publishes a required capture without a named rule")]
    public void RequiredCapturePublishesWithoutANamedRule()
    {
        var host = Host(recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)]);

        host.ValidateForPublish(Sealed(CaptureSurface(new(true, []))), AllowAccess.Instance);
    }

    [Fact(DisplayName = "layout-bound-8: omitting the host validation-rule register fails closed")]
    public void OmittedValidationRuleRegisterRefusesTheSameReleasedRule()
    {
        var host = Host(
            recordFields: [new("invoice.reference", FieldScalarValueShape.Text, HasValueDomain: false)],
            options: new(SupplyValidationRules: false));

        AssertRefused(
            () => host.ValidateForPublish(Sealed(CaptureSurface(new(false, ["rules.value-required"]))), AllowAccess.Instance),
            "layout.capture.validation_rule_unknown");
    }

    [Fact(DisplayName = "layout-bound-3: the host supplies released controls at render and refuses an unregistered control")]
    public async Task ReleasedAndUnknownFieldControlsGoThroughTheHostRenderSeam()
    {
        var host = Host();

        await RenderAsync(host, Sealed(CaptureSurface(new(false, [], Control: new("text")))));

        await AssertRenderRefusedAsync(host, Sealed(CaptureSurface(new(false, [], Control: new("signature")))), LayoutDefinitionCodes.FieldControlUnknown);
    }

    [Fact(DisplayName = "layout-bound-3: omitting the host field-control register at render fails closed")]
    public async Task OmittedFieldControlRegisterRefusesTheReleasedControlAtRender()
    {
        var host = Host(options: new(SupplyFieldControls: false));

        await AssertRenderRefusedAsync(host, Sealed(CaptureSurface(new(false, [], Control: new("text")))), LayoutDefinitionCodes.FieldControlUnknown);
    }

    [Fact(DisplayName = "layout-bound-7: the host supplies installed-pack pages at render and refuses an unknown page")]
    public async Task InstalledPackPagesGoThroughTheHostRenderSeam()
    {
        var host = Host(pageLayouts: [PackLayout], pageMasters: [PackMaster]);

        var rendered = await RenderAsync(host, Sealed(PageSurface(new("run", PackLayout.Id, PackMaster.Id, ["body"]))));

        Assert.Equal(PackMaster.Id, Assert.Single(rendered.Plan.PageFragments!).PageMasterId);
        await AssertRenderRefusedAsync(host, Sealed(PageSurface(new("run", "pack.unknown", PackMaster.Id, ["body"]))), LayoutDefinitionCodes.PageReferenceUnknown);
    }

    [Fact(DisplayName = "layout-bound-7: an empty host page register at render fails closed")]
    public async Task EmptyPageRegisterRefusesTheInstalledPackCitationAtRender()
    {
        await AssertRenderRefusedAsync(Host(), Sealed(PageSurface(new("run", PackLayout.Id, PackMaster.Id, ["body"]))), LayoutDefinitionCodes.PageReferenceUnknown);
    }

    [Fact(DisplayName = "layout-bound-8: the host supplies released validation rules at render and refuses an unregistered rule")]
    public async Task ReleasedAndUnknownValidationRulesGoThroughTheHostRenderSeam()
    {
        var host = Host();

        await AssertRenderRefusedAsync(host, Sealed(CaptureSurface(new(false, ["rules.value-required"]))), "layout.capture.validation_rule_unknown");

        await AssertRenderRefusedAsync(host, Sealed(CaptureSurface(new(false, ["rules.unknown"]))), "layout.capture.validation_rule_unknown");
    }

    /// <summary>
    /// Named rules fail closed at publication only (T-724 ruling 36; platform
    /// LayoutDefinitionAdmission.cs:448-451). Render admits only an already-published version
    /// (DES-0053 design.md:90-91) and does not repeat publication's checks (DES-0018 ruling 6), so a
    /// host that omits the rule register still renders a surface naming a rule.
    /// </summary>
    [Fact(DisplayName = "layout-bound-8: omitting the host validation-rule register at render admits the published surface (T-724 ruling 36, DES-0053 :90-91, DES-0018 ruling 6)")]
    public async Task OmittedValidationRuleRegisterAdmitsThePublishedSurfaceAtRender()
    {
        var host = Host(options: new(SupplyValidationRules: false));

        var rendered = await RenderAsync(host, Sealed(CaptureSurface(new(false, ["rules.value-required"]))));

        Assert.Equal("reference", Assert.Single(rendered.Plan.Flow).BlockId);
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

    private static void AssertRefused(Action publish, string code, string? pointer = null)
    {
        var refused = Assert.Throws<DefinitionRefusalException>(publish);
        Assert.Contains(refused.Refusals, refusal => refusal.Code == code && (pointer is null || refusal.Pointer == pointer));
    }

    // The store admits any body, so the render seam alone decides: a body persisted under other
    // registers reaches render exactly as the published store hands it over.
    private static async Task<LayoutResolvedSurface> RenderAsync(LayoutPublishRegisters host, LayoutDefinition definition)
    {
        var key = new DefinitionKey("tenant-733", DefinitionKind.Layout, definition.Envelope.Identity);
        var store = new InMemoryVersionedDefinitionStore(new Dictionary<DefinitionKind, DefinitionAdmission>
        {
            [DefinitionKind.Layout] = (_, _) => [],
        });
        await store.SaveDraftAsync(new(key, "version-1", "1.0.0", Encoding.UTF8.GetString(LayoutDefinitionJson.SerializeCanonical(definition))), 0, "draft-1");
        await store.PublishAsync(key, "version-1", 1, "publish-1");
        return await host.RenderResolver(store).ResolveAsync(new(key, "version-1"), AllowAccess.Instance, AllowAccess.Instance);
    }

    private static async Task AssertRenderRefusedAsync(LayoutPublishRegisters host, LayoutDefinition definition, string code)
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RenderAsync(host, definition));
        Assert.Equal("layout.persisted_body_invalid", refused.Message);
        Assert.Contains(Assert.IsType<DefinitionRefusalException>(refused.InnerException).Refusals, refusal => refusal.Code == code);
    }

    private sealed class AllowAccess : ILayoutAccess, ILayoutSubmitAccess
    {
        public static AllowAccess Instance { get; } = new();
        public bool CanAuthor() => true;
        public bool CanPublish() => true;
        public bool CanRead(LayoutBinding binding) => true;
        public bool CanOpen(string surfaceId) => true;
        public bool Satisfies(SubmitGate gate) => true;
        public bool CanWrite() => true;
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
