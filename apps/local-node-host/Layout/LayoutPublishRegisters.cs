using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>
/// The Layout content supplied by installed packs and the node's record catalogue at publication.
/// T-734 will add a dedicated pack content kind for page declarations; until then this is the host
/// seam that carries the page declarations its pack projection has discovered.
/// </summary>
public sealed record LayoutPublishState(
    IReadOnlyList<LayoutRecordFieldDescriptor>? RecordFields = null,
    IReadOnlyList<LayoutPageLayoutDefinition>? InstalledPackPageLayouts = null,
    IReadOnlyList<LayoutPageMasterDefinition>? InstalledPackPageMasters = null);

/// <summary>
/// Selects which bound registers the host supplies. Production uses the default; the explicit
/// omissions make the host's fail-closed boundary directly provable in-process.
/// </summary>
public sealed record LayoutPublishRegisterOptions(
    bool SupplyFieldControls = true,
    bool SupplyPages = true,
    bool SupplyValidationRules = true);

/// <summary>
/// T-733 Layout host seam. It binds the node's released controls and validation rules, plus
/// pack-declared pages and record fields, before platform admission runs at publish and before the
/// platform resolver derives a plan at render, so both stages judge a surface against one set of
/// registers. T-735 will compose this seam into the HTTP/DI routes; it is deliberately usable in-process now.
/// </summary>
public sealed class LayoutPublishRegisters(
    LayoutPublishState state,
    LayoutPublishRegisterOptions? options = null)
{
    private readonly LayoutPublishRegisterOptions _options = options ?? new();
    private readonly Action<LayoutDefinition, LayoutHostRegisters, ILayoutAccess> _admit = LayoutDefinitionAdmission.ValidateForPublish;

    // Lets the host boundary tests observe the admission input even when a released register is empty.
    internal LayoutPublishRegisters(
        LayoutPublishState state,
        LayoutPublishRegisterOptions? options,
        Action<LayoutDefinition, LayoutHostRegisters, ILayoutAccess> admit)
        : this(state, options)
    {
        _admit = admit;
    }

    /// <summary>Builds the actual platform admission input from this node's publish state.</summary>
    public LayoutHostRegisters Build()
    {
        var controls = _options.SupplyFieldControls ? LayoutFieldControlRegistry.Released : null;
        var pages = _options.SupplyPages
            ? new LayoutPageRegistry(state.InstalledPackPageLayouts ?? [], state.InstalledPackPageMasters ?? [])
            : null;
        var rules = _options.SupplyValidationRules ? LayoutValidationRuleRegistry.Released : null;

        return new LayoutHostRegisters(
            LayoutBlockKindRegistry.Platform,
            FieldControls: controls,
            Pages: pages,
            ValidationRules: rules,
            // Publication checks a named control against the actual record field it captures. The
            // catalogue can be empty, but it remains a real register rather than a permissive fallback.
            Fields: controls is null ? null : new LayoutRecordFieldRegistry(state.RecordFields ?? []));
    }

    /// <summary>Runs the platform's one publish-time admission path against the host-built registers.</summary>
    public void ValidateForPublish(LayoutDefinition definition, ILayoutAccess author)
        => _admit(definition, Build(), author);

    /// <summary>
    /// The render-time half of the seam: the platform's published-surface resolver over
    /// <paramref name="store"/>, holding the same registers <see cref="Build"/> supplies at publish.
    /// A persisted body naming a control, page or validation rule these registers lack is refused
    /// with <c>layout.persisted_body_invalid</c>, its refusals on the inner exception, before any plan
    /// is derived; pack page citations flow against the supplied page register.
    /// </summary>
    /// <param name="store">The shared definition store the published surface is read from.</param>
    public LayoutPublishedSurfaceResolver RenderResolver(IVersionedDefinitionStore store)
        => new(store, registers: Build());
}
