using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 260 slice 20 — THE retired-family spelling fence. One table-driven class, one root set,
/// one extension set. It absorbs the three per-slice content fences that preceded it
/// (the foundation/Blazor renamed-type fence of slices 5, 8, 9, 10-13 and 16; the persistence
/// entity-module and catalog-key fence of slices 4 and 17; the interop/schema/protocol literal
/// fence of slices 16, 18 and 19), because three fences meant three root sets and three extension
/// sets, and every review round found another directory or extension one of them could not see.
/// <para>
/// <b>Root set.</b> EVERY tracked directory at the repository root that can carry source or docs,
/// discovered by walking the root rather than named one at a time — the earlier fences scanned
/// <c>apps</c>+<c>packages</c> (+<c>tooling</c>, +<c>protocol</c>/<c>docs</c>), leaving
/// <c>eng</c>, <c>hosts</c>, <c>src</c>, <c>tests</c>, <c>_shared</c>, <c>.github</c> and the
/// repository-root files unscanned. Build and tool output (<c>obj</c>, <c>bin</c>,
/// <c>node_modules</c>, <c>dist</c>, <c>.git</c>, agent scratch) is excluded structurally.
/// </para>
/// <para>
/// <b>Extension set.</b> The union of every extension any predecessor fence scanned plus the ones
/// their review notes named as missing (<c>.js</c>, <c>.css</c>, <c>.tsv</c>, <c>.props</c>,
/// <c>.targets</c>, <c>.slnx</c>, <c>.resx</c>, <c>.json</c>, <c>.yml</c>), i.e. every text
/// extension tracked in this repository.
/// </para>
/// <para>
/// <b>Rows.</b> Two kinds. An IDENTIFIER row matches on an identifier boundary, so a live longer
/// name that merely starts with a retired one is not an offender. A LITERAL row matches as a plain
/// substring in the file's content AND in its repository-relative path, so a renamed asset that
/// kept its old file name is not invisible. Retired spellings are reconstructed from code points
/// so this file does not itself carry one.
/// </para>
/// <para>
/// <b>Exemptions.</b> Exact repo-relative paths only, one row per file with a class and a reason
/// (<see cref="ExemptPaths"/>), and the rows are registered with the ticket-257 vacuity property so
/// a row that stops matching goes red. There is no wildcard and no per-symbol allow-list left: the
/// four per-slice twin allow-lists the component slices carried are all empty, and they are
/// replaced here by ONE no-twin guard (<see cref="TwinAllowList"/>).
/// </para>
/// </summary>
public sealed class RetiredFamilySpellingFenceTests
{
    private static string FromCodePoints(params int[] codes) => new([.. codes.Select(code => (char)code)]);

    // The retired family words. `Retired` is the PascalCase F5 word every identifier row is built from.
    private static readonly string Retired = FromCodePoints(83, 104, 105, 112, 121, 97, 114, 100);
    private static readonly string F5 = Retired.ToLowerInvariant();
    private static readonly string F5Upper = Retired.ToUpperInvariant();
    private static readonly string F1 = FromCodePoints(115, 117, 110, 102, 105, 115, 104);
    private static readonly string F2 = FromCodePoints(99, 97, 114, 114, 105, 101, 114);
    private static readonly string F2Pascal = FromCodePoints(67, 97, 114, 114, 105, 101, 114);
    private static readonly string F1Pascal = FromCodePoints(83, 117, 110, 102, 105, 115, 104);
    private static readonly string F3 = FromCodePoints(104, 117, 108, 108);
    private static readonly string F3Pascal = FromCodePoints(72, 117, 108, 108);

    /// <summary>One row per retired identifier: the exact old spelling of a type this slice renamed.</summary>
    private static readonly string[] RetiredIdentifiers =
    [
        // Configuration (5)
        Retired + "Theme",
        Retired + "Options",
        Retired + "Shape",
        Retired + "ColorPalette",
        Retired + "TypographyScale",
        // Localization (3)
        "I" + Retired + "Localizer",
        Retired + "Localizer",
        Retired + "LocalizerFactory",
        // Services (7)
        "I" + Retired + "BreakpointService",
        "I" + Retired + "DialogService",
        "I" + Retired + "NotificationService",
        "I" + Retired + "OverlayService",
        "I" + Retired + "ThemeService",
        Retired + "NotificationService",
        Retired + "ThemeService",
        // Models (3)
        Retired + "MouseEventArgs",
        Retired + "ResizeEventArgs",
        Retired + "ObservedSizeChangedEventArgs",
        // Extensions (1) + the adapter-boundary conversion method named after the retired word
        Retired + "DecentralizationExtensions",
        "To" + Retired,
        // Blazor component base (1) - slice 8; every component derives from it.
        Retired + "ComponentBase",
    ];


    /// <summary>
    /// Slice 9 rows: every component type declared under
    /// <c>packages/ui-adapters-blazor/Components/DataDisplay</c>, renamed with its files.
    /// Kept as its own array so slices landing in parallel append to their own table.
    /// </summary>
    private static readonly string[] RetiredDataDisplayComponentIdentifiers =
    [
        Retired + "AllocationScheduler",
        Retired + "ArcGauge",
        Retired + "Avatar",
        Retired + "Badge",
        Retired + "BadgeContainer",
        Retired + "Barcode",
        Retired + "Calendar",
        Retired + "Card",
        Retired + "CardActions",
        Retired + "CardBody",
        Retired + "CardFooter",
        Retired + "CardHeader",
        Retired + "CardImage",
        Retired + "CircularGauge",
        Retired + "CodeBlock",
        Retired + "ColorDot",
        Retired + "ColumnBase",
        Retired + "CurrencyAmount",
        Retired + "DataGrid",
        Retired + "DataSheet",
        Retired + "DataSheetColumn",
        Retired + "DescriptionList",
        Retired + "Gantt",
        Retired + "GanttDependencies",
        Retired + "GridColumn",
        Retired + "GridColumnMenu",
        Retired + "GridCommandButton",
        Retired + "GridToolbar",
        Retired + "Highlight",
        Retired + "Highlighter",
        Retired + "Image",
        Retired + "LinearGauge",
        Retired + "List",
        Retired + "ListItem",
        Retired + "ListView",
        Retired + "Map",
        Retired + "NotificationDot",
        Retired + "NumberBadge",
        Retired + "PdfViewer",
        Retired + "PivotGrid",
        Retired + "PivotGridColumnField",
        Retired + "PivotGridConfigurator",
        Retired + "PivotGridConfiguratorButton",
        Retired + "PivotGridContainer",
        Retired + "PivotGridMeasureField",
        Retired + "PivotGridRowField",
        Retired + "Popover",
        Retired + "PropertyCard",
        Retired + "QRCode",
        Retired + "RadialGauge",
        Retired + "Sankey",
        Retired + "Scheduler",
        Retired + "SearchHighlight",
        Retired + "Spreadsheet",
        Retired + "StatusPill",
        Retired + "Table",
        Retired + "Tag",
        Retired + "Timeline",
        Retired + "TimelineItem",
        Retired + "Tooltip",
        Retired + "TreeList",
        Retired + "TreeListColumn",
        Retired + "Typography",
    ];



    /// <summary>
    /// Slice 13 rows: every type declared under <c>packages/ui-adapters-blazor/Components/</c>
    /// {AI, Charts, Editors, LocalFirst, Media, Overlays, Scheduling, Showcase, Structural},
    /// renamed with its files. The six names shared with the slice-9 DataDisplay inventory
    /// (Barcode, Gantt, Map, PdfViewer, Scheduler, Spreadsheet) are already scanned for there and
    /// are not repeated here; slice 13 renamed their out-of-slice twins.
    /// </summary>
    private static readonly string[] RetiredSlice13ComponentIdentifiers =
    [
        Retired + "AIPrompt",
        Retired + "Chat",
        Retired + "ConversationList",
        Retired + "InlineAIPrompt",
        Retired + "PromptBox",
        Retired + "SmartPasteButton",
        Retired + "SpeechToTextButton",
        Retired + "Chart",
        Retired + "ChartSeries",
        Retired + "Gauge",
        Retired + "StockChart",
        Retired + "Editor",
        Retired + "EditorServiceExtensions",
        Retired + "ConflictList",
        Retired + "FreshnessBadge",
        Retired + "NodeHealthBar",
        Retired + "OfflineIndicator",
        Retired + "OptimisticButton",
        Retired + "SyncStateBadge",
        Retired + "SyncStatusIndicator",
        Retired + "TeamSwitcher",
        Retired + "PdfEditor",
        Retired + "Tour",
        Retired + "TourStep",
        Retired + "Popup",
        Retired + "Window",
        Retired + "ExamplePanel",
        Retired + "ExamplePanelLabels",
        Retired + "SourceFile",
        Retired + "ColumnVisibilityMenu",
        Retired + "ListToolbar",
        Retired + "SavedViewsMenu",
        Retired + "SortControl",
        Retired + "SortDirection",
        Retired + "SortState",
        Retired + "SortOption",
        Retired + "ColumnVisibilityOption",
        Retired + "SavedView",
        Retired + "SavedViewState",
    ];

    /// <summary>
    /// Slice 11 rows: every component type declared under
    /// <c>packages/ui-adapters-blazor/Components/Layout</c> and <c>Components/Navigation</c>,
    /// renamed with its files. Kept as its own array so slices landing in parallel append to
    /// their own table.
    /// </summary>
    private static readonly string[] RetiredLayoutNavigationComponentIdentifiers =
    [
        Retired + "Accordion",
        Retired + "AccordionItem",
        Retired + "ActionSheet",
        Retired + "ActivityLog",
        Retired + "AnimationContainer",
        Retired + "AppBar",
        Retired + "AppLayout",
        Retired + "AspectRatio",
        Retired + "BottomNavigation",
        Retired + "Breadcrumb",
        Retired + "BreadcrumbItem",
        Retired + "Carousel",
        Retired + "CarouselSlide",
        Retired + "Collapsible",
        Retired + "Column",
        Retired + "CommandPalette",
        Retired + "Container",
        Retired + "ContextMenu",
        Retired + "Divider",
        Retired + "DockManager",
        Retired + "DockPane",
        Retired + "DockSplit",
        Retired + "Drawer",
        Retired + "EnvironmentBadge",
        Retired + "GlobalSearch",
        Retired + "GridLayout",
        Retired + "GridLayoutColumn",
        Retired + "GridLayoutItem",
        Retired + "GridLayoutRow",
        Retired + "MegaMenu",
        Retired + "Menu",
        Retired + "MenuDivider",
        Retired + "MenuItem",
        Retired + "Menubar",
        Retired + "NavBar",
        Retired + "NavItem",
        Retired + "NavMenu",
        Retired + "NavigationMenu",
        Retired + "NotificationCenter",
        Retired + "Page",
        Retired + "PageHeader",
        Retired + "Pagination",
        Retired + "Panel",
        Retired + "ResizableContainer",
        Retired + "Row",
        Retired + "ScrollArea",
        Retired + "Separator",
        Retired + "Sheet",
        Retired + "SideNav",
        Retired + "Splitter",
        Retired + "SplitterPane",
        Retired + "SplitterPanes",
        Retired + "Stack",
        Retired + "Step",
        Retired + "StepBase",
        Retired + "Stepper",
        Retired + "StepperStep",
        Retired + "StepperSteps",
        Retired + "TabStrip",
        Retired + "Tile",
        Retired + "TileLayout",
        Retired + "TimeRangeSelector",
        Retired + "Toolbar",
        Retired + "ToolbarButton",
        Retired + "ToolbarGroup",
        Retired + "ToolbarSeparator",
        Retired + "ToolbarToggleButton",
        Retired + "TreeItem",
        Retired + "TreeView",
        Retired + "Wizard",
        Retired + "WizardStep",
        Retired + "WizardSteps",
        Retired + "WorkspaceShell",
    ];



    /// <summary>
    /// Slice 12 rows: every component type declared under
    /// <c>packages/ui-adapters-blazor/Components/Feedback</c>, <c>Components/Buttons</c> and
    /// <c>Components/Utility</c>, renamed with its files. Kept as its own array so slices landing
    /// in parallel append to their own table.
    /// </summary>
    private static readonly string[] RetiredFeedbackButtonsUtilityComponentIdentifiers =
    [
        Retired + "ActionMenu",
        Retired + "Alert",
        Retired + "AlertStrip",
        Retired + "Button",
        Retired + "ButtonGroup",
        Retired + "Callout",
        Retired + "Chat",
        Retired + "Chip",
        Retired + "ChipSet",
        Retired + "ChunkProgressBar",
        Retired + "ConfirmDialog",
        Retired + "DataBanner",
        Retired + "DataExportButton",
        Retired + "DataToast",
        Retired + "Diagram",
        Retired + "Dialog",
        Retired + "DropDownButton",
        Retired + "DropZone",
        Retired + "EmptyState",
        Retired + "ErrorCard",
        Retired + "Fab",
        Retired + "FilterChips",
        Retired + "FloatingLabel",
        Retired + "GuardedControl",
        Retired + "HoverCard",
        Retired + "Icon",
        Retired + "IconButton",
        Retired + "Loader",
        Retired + "LoaderContainer",
        Retired + "LoadingState",
        Retired + "MaskedText",
        Retired + "MediaQuery",
        Retired + "PdfExport",
        Retired + "ProgressBar",
        Retired + "ProgressCircle",
        Retired + "Ripple",
        Retired + "RoleGate",
        Retired + "SegmentedControl",
        Retired + "SignalRConnectionStatus",
        Retired + "Skeleton",
        Retired + "Snackbar",
        Retired + "SnackbarHost",
        Retired + "Sonner",
        Retired + "Spinner",
        Retired + "SplitButton",
        Retired + "StatusBanner",
        Retired + "ThemeProvider",
        Retired + "Toast",
        Retired + "Toaster",
        Retired + "ToggleButton",
    ];



    /// <summary>
    /// Slice 10 rows: every component/adapter type declared under
    /// <c>packages/ui-adapters-blazor/Components/Forms</c> (Containers, Fields, Inputs, Schema),
    /// renamed with its files. Kept as its own array so slices landing in parallel append to
    /// their own table. <c>ColorPalette</c> is absent because the slice-5 table above already
    /// carries that spelling; slice 10 renamed the component that collided with it.
    /// </summary>
    private static readonly string[] RetiredFormsComponentIdentifiers =
    [
        Retired + "Autocomplete",
        Retired + "Checkbox",
        Retired + "CheckboxField",
        Retired + "ColorGradient",
        Retired + "ColorPicker",
        Retired + "ComboBox",
        Retired + "ComboBoxField",
        Retired + "CreditCardBrand",
        Retired + "CreditCardField",
        Retired + "CreditCardValue",
        Retired + "CurrencyField",
        Retired + "DateField",
        Retired + "DateInput",
        Retired + "DatePicker",
        Retired + "DateRangePicker",
        Retired + "DateTimePicker",
        Retired + "DropDownList",
        Retired + "DropDownTree",
        Retired + "DurationField",
        Retired + "ESignatureField",
        Retired + "Editor",
        Retired + "Field",
        Retired + "FieldArray",
        Retired + "FieldArrayContext",
        Retired + "FieldBase",
        Retired + "FieldContext",
        Retired + "FileManager",
        Retired + "FileUpload",
        Retired + "Filter",
        Retired + "FlatColorPicker",
        Retired + "Form",
        Retired + "FormField",
        Retired + "FormSection",
        Retired + "InlineEdit",
        Retired + "Input",
        Retired + "Label",
        Retired + "LabelVariant",
        Retired + "ListBox",
        Retired + "MaskedInput",
        Retired + "MonthYearPicker",
        Retired + "MultiColumnComboBox",
        Retired + "MultiSelect",
        Retired + "MultiSelectTree",
        Retired + "NumberField",
        Retired + "NumberFormatField",
        Retired + "NumberStepper",
        Retired + "NumericInput",
        Retired + "PercentageField",
        Retired + "PhoneField",
        Retired + "PinField",
        Retired + "Radio",
        Retired + "RadioGroup",
        Retired + "RangeSlider",
        Retired + "Rating",
        Retired + "ReadonlyField",
        Retired + "SearchBox",
        Retired + "SearchInput",
        Retired + "SearchableSelect",
        Retired + "Select",
        Retired + "SelectField",
        Retired + "Signature",
        Retired + "SignatureStub",
        Retired + "Slider",
        Retired + "Switch",
        Retired + "SwitchField",
        Retired + "TagInput",
        Retired + "TextArea",
        Retired + "TextAreaField",
        Retired + "TextBox",
        Retired + "TextField",
        Retired + "TimePicker",
        Retired + "UnresolvedControl",
        Retired + "Upload",
        Retired + "UploadChunkSettings",
        Retired + "Validation",
        Retired + "ValidationMessage",
        Retired + "ValidationSummary",
        Retired + "ValidationTooltip",
    ];


    /// <summary>
    /// Slice 16 rows: the four adapter-seam interfaces and the widget descriptor declared under
    /// <c>packages/ui-core/Contracts</c>, renamed with their files, plus the two names that
    /// appeared only as prose citations of a future type (<c>RenderOutput</c>, <c>Pager</c>) and
    /// the two Blazor-track seam names cited by the contract docs (<c>LocaleProvider</c>,
    /// <c>FieldWrapper</c>). Kept as its own array so slices landing in parallel append to their
    /// own table.
    /// </summary>
    private static readonly string[] RetiredUiCoreContractIdentifiers =
    [
        "I" + Retired + "CssProvider",
        "I" + Retired + "IconProvider",
        "I" + Retired + "JsInterop",
        "I" + Retired + "Renderer",
        Retired + "WidgetDescriptor",
        Retired + "RenderOutput",
        Retired + "Pager",
        Retired + "LocaleProvider",
        Retired + "FieldWrapper",
    ];

    /// <summary>
    /// Identifier rows absorbed from the slice-18/19 interop fence: the rule-engine JsonLogic
    /// constant and types, and the protocol identifiers slice 18 renamed in the schema, the
    /// generated bindings and the validation seam. The F2 spellings slice 18 deliberately left LIVE
    /// (the wire operation ids, the protocol id, the security scheme key, the served schema
    /// <c>$id</c>, the persisted bundle artifact identity, the required-check name, the environment
    /// prefix, the crate name and the generated host constant family derived from the exempt
    /// operation ids) are NOT rows, and their absence from this table is what documents them.
    /// </summary>
    private static readonly string[] RetiredProtocolAndSchemaIdentifiers =
    [
        Retired + "JsonLogic",
        Retired + "JsonLogicV1",
        F5Upper + "_JSONLOGIC_V1",
        F2Pascal + "SyncStatus",
        F2Pascal + "Protocol",
        F2Pascal + "ProtocolBoundaryError",
        F2Pascal + "ApplicationRoutes",
        F2Pascal + "HostCommands",
        F2Pascal + "HostPort",
        F2Pascal + "ApplicationPort",
    ];

    /// <summary>
    /// Slice 23 rows: the two areas no earlier slice owned. The seven
    /// <c>packages/ui-adapters-blazor/Shell</c> components (a sibling of <c>Components/</c>, which is
    /// why the per-area slices 9-15 never reached it) and the five public types of the in-repo
    /// <c>packages/client-dotnet</c> client. Both were live code, not prose.
    /// </summary>
    private static readonly string[] RetiredShellAndClientIdentifiers =
    [
        // ui-adapters-blazor/Shell components (7)
        Retired + "AppShell",
        Retired + "AppShellNavGroup",
        Retired + "AppShellNavLink",
        Retired + "AppShellSlideOver",
        Retired + "AccountMenu",
        Retired + "NotificationBell",
        Retired + "UserMenu",
        // client-dotnet public types (5)
        "I" + Retired + "Client",
        Retired + "Client",
        Retired + "ClientMessages",
        Retired + "Permissions",
        Retired + "PermissionDeniedException",
    ];

    /// <summary>
    /// Slice 22 row: the forms overlay record declared in
    /// <c>packages/foundation-forms/Models</c>, renamed with its file. Its name never travelled as
    /// a string — the record serialises by member, so there is no wire or persisted spelling to
    /// migrate — which is why this is a bare rename and not an accept-both window.
    /// </summary>
    private static readonly string[] RetiredFormsOverlayIdentifiers =
    [
        Retired + "Overlay",
    ];

    /// <summary>
    /// Slice C rows: the Blazor JS module-loader interface and class in
    /// <c>packages/ui-adapters-blazor/Internal/Interop</c> and the cascaded log-level enum in
    /// <c>packages/ui-adapters-blazor/Enums</c>, each renamed with its file and every call site.
    /// The enum's cascading-parameter NAME moved with the type: it is an in-process cascade key,
    /// never a persisted or served value, and its only writer and reader are both in
    /// <c>HarborlineThemeProvider</c>.
    /// </summary>
    private static readonly string[] RetiredJsModuleLoaderIdentifiers =
    [
        "I" + Retired + "JsModuleLoader",
        Retired + "JsModuleLoader",
        Retired + "LogLevel",
    ];

    /// <summary>
    /// Slice D rows: the F1-spelled component type names that survived only inside JS and scoped-CSS
    /// DOC HEADERS under <c>packages/ui-adapters-blazor</c>. The types themselves were renamed by the
    /// component slices; these headers were the last places naming them by the retired spelling, so a
    /// header is the reintroduction shape this table has to see.
    /// </summary>
    private static readonly string[] RetiredDocHeaderIdentifiers =
    [
        F1Pascal + "AllocationScheduler",
        F1Pascal + "DataGrid",
        F1Pascal + "DataSheet",
        F1Pascal + "ExamplePanel",
        F1Pascal + "FloatingLabel",
        F1Pascal + "Gantt",
        F1Pascal + "ResizableContainer",
        F1Pascal + "ThemeProvider",
    ];

    /// <summary>
    /// Ticket 260 slices S-E, S-F, S-G and S-H rows. The reports provisionality interface renamed
    /// with its file (its platform mirror under
    /// <c>projections/dotnet/blocks/hlp.blocks.reports</c> renames on ticket 256 to the same
    /// spelling), the three Authoritative taxonomy seed members, the host MSBuild property, and the
    /// pre-rename fixture model name the Rust contract crate used to assert absent by hand — the
    /// assertion is gone because this row states it repository-wide instead of in one crate.
    /// The <c>ActorId</c> sentinel is a LITERAL row (<c>ActorId.&lt;F1&gt;</c>) rather than a bare
    /// identifier row: the bare F1 spelling is still live PROSE elsewhere, so an identifier row on
    /// it would fail on documentation no slice here owns.
    /// </summary>
    private static readonly string[] RetiredSliceEfghIdentifiers =
    [
        "IReportProvisionality" + F2Pascal,
        Retired + "SignatureScopes",
        Retired + "LeasingJurisdictionRules",
        Retired + "VendorSpecialties",
        Retired + "MauiEnabled",
        F3Pascal + "InvokeRequest",
    ];

    private static readonly string[] AllRetiredIdentifiers =
        [.. RetiredIdentifiers, .. RetiredDataDisplayComponentIdentifiers, .. RetiredFormsComponentIdentifiers,
         .. RetiredLayoutNavigationComponentIdentifiers, .. RetiredSlice13ComponentIdentifiers,
         .. RetiredFeedbackButtonsUtilityComponentIdentifiers, .. RetiredUiCoreContractIdentifiers,
         .. RetiredProtocolAndSchemaIdentifiers, .. RetiredShellAndClientIdentifiers,
         .. RetiredFormsOverlayIdentifiers, .. RetiredJsModuleLoaderIdentifiers,
         .. RetiredDocHeaderIdentifiers, .. RetiredSliceEfghIdentifiers];

    /// <summary>The fourteen Blazor JS interop module stems slice 19 renamed.</summary>
    private static readonly string[] JsModuleStems =
    [
        "a11y", "clipboard-download", "datasheet", "dialog-a11y", "dragdrop", "dropzone",
        "form-factor", "gantt", "graphics", "map", "measurement", "observers", "positioning", "resize",
    ];

    /// <summary>
    /// LITERAL rows — keys, ids and paths, matched as a plain substring in the file's content and in
    /// its repository-relative path (an identifier boundary would not apply to any of these). Absorbed
    /// from the slice-4/17 entity-module fence and the slice-18/19 interop fence.
    /// </summary>
    private static readonly string[] RetiredLiterals =
    [
        // Slice 17: the catalog module-key prefix (now `harborline.blocks.`).
        F1 + ".blocks.",
        // Slice 19: JS interop module paths, and the wwwroot asset files that carry the same stem.
        .. JsModuleStems.Select(stem => $"js/{F5}-{stem}.js"),
        // Slice 19: entity-module keys, engine-room instrument names, the scheduling draft schema id.
        F1 + ".local-node.",
        F1 + ".engine_room.",
        F5 + ".scheduling-definition-draft",
        // Slice 18: the directory, package and file paths the protocol family renamed.
        F2 + "-contract-codegen",
        F2 + "-sdk",
        F2 + "-catalog",
        "editions/" + F2 + ".ts",
        F2 + "-protocol.generated",
        F2 + "-sync-status",
        // Slice 18: the edition runtime token, the correlation-id prefix and the renamed test-class stem.
        F2 + "-runtime",
        F2 + "-demo-cp-op-",
        F2Pascal + "Dotnet",
        // Slice D: the Blazor DOM id stems, the CSS class-name stems, the CSS custom-property prefix
        // and the browser global. None is an identifier, and every reader of each one lived in
        // packages/ui-adapters-blazor and moved with it, so each is a plain literal row.
        F5 + "-field-",
        F5 + "-saved-view-",
        F5 + "-sort-",
        F5 + "-schema-unresolved-control",
        F1 + "-helm",
        "--" + F1 + "-",
        "window." + F1Pascal + ".",
        // Slices S-G and S-H: the Authoritative actor sentinel (the MEMBER renamed; its persisted
        // string value "<f1>" is unchanged and is not a row), and the landing script's dead
        // application directory.
        "ActorId." + F1Pascal,
        "apps/" + F3,
    ];

    /// <summary>
    /// The twin allow-list: EMPTY, and it replaces the five per-slice lists the component slices
    /// carried (slice 9 DataDisplay, slice 10 Forms, slice 12 Feedback, slice 13 Editors, and slice 5's
    /// ColorPalette component). Each excused a name borne by an out-of-slice TWIN that a later slice
    /// then renamed with its files, so every one of them was already empty; five empty lists and five
    /// near-identical equality tests are five places for a row to be quietly re-added.
    /// Class: NAME-COLLISION-OUT-OF-SLICE (all discharged). Registered with the ticket-257 vacuity
    /// property, and held empty by <see cref="TheTwinAllowList_IsEmpty"/>.
    /// </summary>
    private static readonly string[] TwinAllowList = [];

    internal static string[] TwinAllowListRows() => TwinAllowList;

    /// <summary>
    /// Exact repo-relative path exemptions — frozen corpora only. No globs, one row per file, each
    /// with a class and a reason. A path that would itself spell a retired family word is held as code
    /// points. Registered with the ticket-257 vacuity property, so a row that stops matching the
    /// fence's own discovery goes red.
    /// </summary>
    private static readonly (string Path, string Class, string Reason)[] ExemptRows =
    [
        ("eng/baselines/"
            + FromCodePoints(104, 117, 108, 108) + "-test-baseline.json",
         "FROZEN-EVIDENCE",
         "Captured suite evidence under eng/baselines: a recorded run's output, pinned by the ratchet. "
         + "Rewriting a baseline to change a name it recorded would falsify the evidence, so this "
         + "corpus sits outside every rename sweep (the 253 sweep already excluded it)."),
    ];

    private static readonly string[] ExemptPaths = [.. ExemptRows.Select(row => row.Path)];

    internal static string[] ExemptPathRows() => ExemptPaths;

    /// <summary>
    /// Ticket 260 slice 20 fix 1 — the directories DECLARED CLEAN of the retired family word.
    /// <para>
    /// The identifier/literal table above is an EXACT-SPELLING fence: it is case-sensitive, it matches
    /// on identifier boundaries, and it only reads <see cref="ScannedExtensions"/>. That is right for a
    /// whole-repository scan (the repository still carries the family word in live code no slice owns),
    /// but it is strictly weaker than what slice 16 had for <c>packages/ui-core</c>: a family-WORD scan,
    /// ANY casing, ANY extension, content and path. ui-core's family surface was custom-element tag
    /// names, <c>data-*</c> sentinels, a Storybook parameter key and a shim element id — none of them
    /// identifiers, none of them table rows. Consolidating that guard into the table lost it, and a
    /// planted lower-case <c>&lt;f5&gt;-button</c> comment under ui-core went green.
    /// </para>
    /// <para>
    /// So the fence keeps BOTH: (a) the exact-identifier table over the one root/extension set, and
    /// (b) this family-word scan over the directories a slice has actually cleared. The list is a
    /// TABLE and it RATCHETS: each future landing appends its directory, and the scan then holds that
    /// directory to the stronger statement for good. It is an inventory of cleared ground, not an
    /// allow-list — a row excuses nothing, it demands more.
    /// </para>
    /// <para>
    /// Only directories that are clean TODAY may be listed. The Blazor component areas of slices 9-11
    /// and the Feedback/Utility areas of slice 12 are NOT here: they still reference
    /// <c>&lt;F5&gt;JsModuleLoader</c> and <c>&lt;F5&gt;LogLevel</c>, live types under
    /// <c>Internal/Interop/</c> and <c>Enums/</c> that no slice has renamed yet (class H of the slice
    /// report). They are appended the day that rename lands.
    /// </para>
    /// </summary>
    private static readonly (string Directory, string Class, string Reason)[] CleanDirectoryRows =
    [
        ("packages/ui-core", "SLICE-16",
         "Slice 16 cleared the ui-core contract surface, including the non-identifier forms (custom "
         + "element tag names, data-* sentinels, the Storybook parameter key, the shim element id) that "
         + "the identifier table cannot express."),
        ("packages/ui-adapters-blazor/Components/Buttons", "SLICE-12",
         "Slice 12 renamed the Buttons area and it carries no family word in any casing or extension."),
        ("packages/ui-adapters-blazor/Components/Editors", "SLICE-13",
         "Slice 13 renamed the Editors area and it carries no family word in any casing or extension."),
        ("packages/ui-adapters-blazor/Components/Structural", "SLICE-D",
         "Slice D renamed the two generated select DOM ids that were this area's last family spelling; "
         + "the area now carries no family word in any casing or extension."),
        ("packages/ui-adapters-blazor/Components/Utility", "SLICE-D",
         "Slice D renamed the scoped-CSS custom properties and the two scoped-CSS doc headers here, "
         + "after slice C took the cascaded log-level enum; the area is now clear."),
        ("packages/ui-adapters-blazor/Wayfinder", "SLICE-D",
         "Slice D renamed the helm CSS class-name family, the only family spelling this area had."),
        ("packages/ui-adapters-blazor/wwwroot", "SLICE-D",
         "Slice D renamed the five JS interop module doc headers; the asset file stems themselves were "
         + "already renamed by slice 19, so the served asset tree is now clear."),
    ];

    private static readonly string[] CleanDirectories = [.. CleanDirectoryRows.Select(row => row.Directory)];

    internal static string[] CleanDirectoryRowPaths() => CleanDirectories;

    /// <summary>The clean-directory rows that name a directory the repository really has, with files in it.</summary>
    internal static string[] DiscoveredCleanDirectories()
    {
        var root = RepositoryRoot();
        return
        [
            .. CleanDirectories
                .Where(directory => WalkTree(root, directory).Any())
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The fence's own discovery, exemptions bypassed, as <c>path:Match</c> rows.</summary>
    internal static string[] DiscoveredRetiredSpellingSites() =>
        [.. Scan(RepositoryRoot()).Select(hit => $"{hit.Path}:{hit.Name}").Order(StringComparer.Ordinal)];

    /// <summary>The exempt files the fence actually still finds a retired spelling in.</summary>
    internal static string[] DiscoveredExemptPaths() =>
        [.. Scan(RepositoryRoot()).Select(hit => hit.Path).Distinct().Where(ExemptPaths.Contains).Order(StringComparer.Ordinal)];

    [Fact(DisplayName = "Ticket 260 slice 20: no retired family spelling survives anywhere in the repository")]
    public void RetiredSpellingsAreAbsentFromEveryScannedRoot()
    {
        var offenders = Scan(RepositoryRoot())
            .Where(hit => !ExemptPaths.Contains(hit.Path))
            .Select(hit => $"{hit.Path}:{hit.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Sites still carrying a retired family spelling:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact(DisplayName = "Ticket 260 slice 20 fix 1: every directory declared clean carries no family word, in any casing, in any file")]
    public void CleanDirectories_CarryNoFamilyWordInAnyCasingOrExtension()
    {
        var root = RepositoryRoot();
        var offenders = CleanDirectoryRows
            .SelectMany(row => ScanTreeForFamilyWord(root, row.Directory).Select(path => $"{row.Class} {path}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Directories declared clean of the retired family word still carry it (any casing, any "
            + $"extension, content or path):{Environment.NewLine}" + string.Join(Environment.NewLine, offenders));
    }

    [Fact(DisplayName = "Ticket 260 slice 20 fix 1: every clean-directory row names a real directory with files, and carries a class and a reason")]
    public void CleanDirectoryRows_AreNotVacuous()
    {
        Assert.NotEmpty(CleanDirectoryRows);
        Assert.Equal(CleanDirectories.Order(StringComparer.Ordinal).ToArray(), DiscoveredCleanDirectories());
        Assert.All(CleanDirectoryRows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Class));
            Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        });
    }

    [Fact(DisplayName = "Ticket 260 slice 20 fix 1: the clean-directory scan reports a planted family word the identifier table cannot see")]
    public void CleanDirectoryScan_ReportsAPlantedFamilyWordInAnyCasingAndAnyExtension()
    {
        // The exact shapes review 1 planted and found green after the consolidation: a lower-case
        // hyphenated custom-element token in a comment, and a file that carries the word in its NAME
        // only. Neither is an identifier row nor a literal row, so the table scan stays empty for both
        // — which is asserted here, so this test pins WHY the second scan has to exist.
        foreach (var spelling in new[] { F5, Retired, F5Upper })
        {
            WithPlantedRoot("packages", Path.Combine("ui-core", "src"), planted =>
            {
                var root = RootOf(planted, 3);

                var probe = Path.Combine(planted, "planted.ts");
                File.WriteAllText(probe, $"// {spelling}-button custom element, data-{spelling}-root sentinel\n");
                Assert.Equal(["packages/ui-core/src/planted.ts"], ScanTreeForFamilyWord(root, "packages/ui-core").ToArray());
                Assert.Empty(Scan(root));

                File.WriteAllText(probe, "// clean\n");
                Assert.Empty(ScanTreeForFamilyWord(root, "packages/ui-core"));

                // A file whose NAME carries the word but whose content never mentions it, in an
                // extension the identifier table's ScannedExtensions does not read at all.
                var named = Path.Combine(planted, $"{spelling}-dialog.stories.mdx");
                File.WriteAllText(named, "export const noop = () => {};");
                Assert.Equal(
                    [$"packages/ui-core/src/{spelling}-dialog.stories.mdx"],
                    ScanTreeForFamilyWord(root, "packages/ui-core").ToArray());
                Assert.Empty(Scan(root));
                File.Delete(named);

                // A neighbouring directory that is NOT declared clean is not held to this standard.
                var neighbour = Path.Combine(RootOf(planted, 2), "ui-adapters-blazor");
                Directory.CreateDirectory(neighbour);
                File.WriteAllText(Path.Combine(neighbour, "Live.cs"), $"// {spelling}JsModuleLoader\n");
                Assert.Empty(ScanTreeForFamilyWord(root, "packages/ui-core"));
            });
        }
    }

    [Fact(DisplayName = "Ticket 260 slice 20 fix 1: the walk really visits the repository — an explicit file floor per root, not one implicit canary")]
    public void TheWalk_VisitsAFloorOfFilesInEveryScannedRoot()
    {
        var counts = ScannedFileCountsByRoot();
        var short_ = WalkFloors
            .Where(floor => counts.GetValueOrDefault(floor.Root) < floor.MinimumFiles)
            .Select(floor => $"{floor.Root}: visited {counts.GetValueOrDefault(floor.Root)}, floor {floor.MinimumFiles}")
            .ToArray();

        Assert.True(
            short_.Length == 0,
            $"The fence's walk visited fewer files than the floor in these roots, so a green fence "
            + $"would be green over nothing:{Environment.NewLine}" + string.Join(Environment.NewLine, short_));
    }

    [Fact(DisplayName = "Ticket 260 slice 20: the twin allow-list is empty — no component name has an excuse")]
    public void TheTwinAllowList_IsEmpty() => Assert.Empty(TwinAllowListRows());

    [Fact(DisplayName = "Ticket 260 slice 20: every exempt path row is a file the fence really finds a retired spelling in")]
    public void ExemptRows_AreNotVacuous()
    {
        Assert.Equal(ExemptPaths.Order(StringComparer.Ordinal).ToArray(), DiscoveredExemptPaths());
        Assert.All(ExemptRows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Class));
            Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        });
    }

    [Fact(DisplayName = "Ticket 260 slice 20: the fence reports a planted reintroduction of every row, in every newly covered root and extension")]
    public void FenceReportsAPlantedReintroductionOfEveryRow()
    {
        // Every row, in a file under a root the predecessor fences already scanned.
        WithPlantedRoot("packages", "planted", planted =>
        {
            var root = RootOf(planted, 2);
            var file = Path.Combine(planted, "Planted.cs");
            foreach (var name in AllRetiredIdentifiers)
            {
                File.WriteAllText(file, "public sealed class Planted { void M(" + name + " x) { } }");
                Assert.Equal([("packages/planted/Planted.cs", name)], Scan(root).ToArray());
            }

            foreach (var literal in RetiredLiterals)
            {
                File.WriteAllText(file, "// " + literal);
                Assert.Equal([("packages/planted/Planted.cs", literal)], Scan(root).ToArray());
            }
        });

        // One row planted once in each root the predecessor fences could NOT see, and once in each
        // extension their tables omitted. Both are what this slice widened, so both must bite.
        var probe = Retired + "ComponentBase";
        foreach (var newRoot in NewlyCoveredRoots)
        {
            WithPlantedRoot(newRoot, "planted", planted =>
            {
                File.WriteAllText(Path.Combine(planted, "Planted.cs"), "// " + probe);
                Assert.Equal([($"{newRoot}/planted/Planted.cs", probe)], Scan(RootOf(planted, 2)).ToArray());
            });
        }

        foreach (var extension in NewlyCoveredExtensions)
        {
            WithPlantedRoot("packages", "planted", planted =>
            {
                File.WriteAllText(Path.Combine(planted, "planted" + extension), "// " + probe);
                Assert.Equal([($"packages/planted/planted{extension}", probe)], Scan(RootOf(planted, 2)).ToArray());
            });
        }

        // A repository-ROOT file, which no predecessor fence scanned at all.
        WithPlantedRoot("packages", "keep", planted =>
        {
            var root = RootOf(planted, 2);
            File.WriteAllText(Path.Combine(root, "README.md"), "// " + probe);
            Assert.Equal([("README.md", probe)], Scan(root).ToArray());
        });

        // A renamed asset file that keeps its old NAME but never mentions it in its content: the
        // repository-relative path is scanned alongside the text, so the rename is not invisible.
        foreach (var stem in JsModuleStems)
        {
            WithPlantedRoot("packages", Path.Combine("planted", "js"), planted =>
            {
                File.WriteAllText(Path.Combine(planted, $"{F5}-{stem}.js"), "export const noop = () => {};");
                Assert.Equal(
                    [($"packages/planted/js/{F5}-{stem}.js", $"js/{F5}-{stem}.js")],
                    Scan(RootOf(planted, 3)).ToArray());
            });
        }
    }

    /// <summary>
    /// Ported from the slice-23 fence this class absorbed. A Blazor component's type name is its FILE
    /// name, so a reintroduction can carry a retired spelling with nothing in any file's text. Every
    /// identifier row is planted as a file name over empty content; the class-level planted-red test
    /// above plants row text in a file's CONTENT, and only the literal rows are planted as names.
    /// </summary>
    [Fact(DisplayName = "Ticket 260 slice 23: the fence reports a retired spelling carried by a file name alone")]
    public void FenceReportsARetiredSpellingCarriedByAFileNameAlone()
    {
        WithPlantedRoot("packages", "planted", planted =>
        {
            var root = RootOf(planted, 2);
            foreach (var name in AllRetiredIdentifiers)
            {
                var file = Path.Combine(planted, name + ".razor");
                File.WriteAllText(file, "<div></div>" + Environment.NewLine);
                Assert.Equal([($"packages/planted/{name}.razor", name)], Scan(root).ToArray());
                File.Delete(file);
            }
        });
    }

    [Fact(DisplayName = "Ticket 260 slice 20: a checkout whose own path carries a skipped directory name is still scanned")]
    public void SkippedDirectoryNames_AreMatchedOnTheRelativePathOnly()
    {
        // The defect this pins, found by running the fence: the skip test was applied to the ABSOLUTE
        // path, and this repository's worktrees live under `.claude/worktrees/<lane>/`. Every file in
        // the tree matched a skipped fragment, the scan returned nothing, and the fence reported green
        // over an empty walk — while its planted-red tests, which use a short temp root, all passed.
        var temp = Path.Combine(Path.GetTempPath(), "ticket-260-s20-" + Guid.NewGuid().ToString("N"));
        foreach (var skippedName in SkippedDirectories)
        {
            var root = Path.Combine(temp, skippedName, "checkout");
            var planted = Path.Combine(root, "packages", "planted");
            Directory.CreateDirectory(Path.Combine(root, "apps"));
            Directory.CreateDirectory(planted);
            try
            {
                var probe = Retired + "ComponentBase";
                File.WriteAllText(Path.Combine(planted, "Planted.cs"), "// " + probe);
                Assert.Equal([("packages/planted/Planted.cs", probe)], Scan(root).ToArray());

                // The same name INSIDE the tree still skips, which is what the filter is for.
                var inside = Path.Combine(planted, skippedName);
                Directory.CreateDirectory(inside);
                File.WriteAllText(Path.Combine(inside, "Skipped.cs"), "// " + probe);
                Assert.Equal([("packages/planted/Planted.cs", probe)], Scan(root).ToArray());
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Ticket 260 slice 20: an exempt path excuses only itself, and only while the fence still finds it")]
    public void ExemptPath_ExcusesOnlyItself()
    {
        var probe = Retired + "ComponentBase";
        WithPlantedRoot("eng", "baselines", planted =>
        {
            var root = RootOf(planted, 2);
            File.WriteAllText(Path.Combine(root, ExemptPaths[0].Replace('/', Path.DirectorySeparatorChar)), "// " + probe);
            File.WriteAllText(Path.Combine(planted, "neighbour.json"), "// " + probe);

            var found = Scan(root).Select(hit => hit.Path).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal([ExemptPaths[0], "eng/baselines/neighbour.json"], found);
            Assert.Equal([ExemptPaths[0]], found.Where(ExemptPaths.Contains).ToArray());
        });
    }

    [Fact(DisplayName = "Ticket 260 slice 20: the fence does not flag a longer live identifier or a neighbouring literal")]
    public void FenceDoesNotFlagALongerIdentifierOrANeighbouringLiteral()
    {
        WithPlantedRoot("packages", "planted", planted =>
        {
            File.WriteAllText(
                Path.Combine(planted, "Planted.cs"),
                $"public sealed class {Retired}ThemeProviderHost {{ }}\n"
                // Live at this pin and must stay green: the rule-engine conformance corpus file name,
                // the macOS launchd service id, the undrafted scheduling contract id, and a longer
                // identifier that merely starts with a retired identifier row.
                + $"// {F5}-ops.json com.{F1}.local-node-host {F5}.scheduling-definition\n"
                + $"public sealed class {Retired}JsonLogicRuntime {{ }}\n");
            Assert.Empty(Scan(RootOf(planted, 2)));
        });
    }

    private static void WithPlantedRoot(string root, string relative, Action<string> body)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ticket-260-s20-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(temp, root, relative);
        Directory.CreateDirectory(Path.Combine(temp, "apps"));
        Directory.CreateDirectory(Path.Combine(temp, "packages"));
        Directory.CreateDirectory(planted);
        try
        {
            body(planted);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string RootOf(string planted, int levels)
    {
        var directory = new DirectoryInfo(planted);
        for (var level = 0; level < levels; level++) directory = directory.Parent!;
        return directory.FullName;
    }

    // ---- the ONE root set and the ONE extension set -------------------------------------------

    /// <summary>
    /// Directory names never scanned: build output, package caches and agent scratch. A structural
    /// filter on the walk, applied at every depth, not a per-row exception.
    /// </summary>
    private static readonly string[] SkippedDirectories =
        ["obj", "bin", "node_modules", "dist", ".git", ".vs", ".vite", ".artifacts", "artifacts", ".packages", ".claude", ".wolf", ".codex"];

    /// <summary>
    /// Roots the three predecessor fences could not see. Named here only so the planted-red test can
    /// prove the widening bites in each of them; the walk itself does not read this list.
    /// </summary>
    private static readonly string[] NewlyCoveredRoots = ["eng", "hosts", "src", "tests", "_shared", ".github"];

    /// <summary>
    /// Extensions the predecessor fences' tables omitted, from their review notes. Named here only so
    /// the planted-red test can prove each one is now scanned; the walk reads
    /// <see cref="ScannedExtensions"/>.
    /// </summary>
    private static readonly string[] NewlyCoveredExtensions =
        [".js", ".css", ".tsv", ".props", ".targets", ".slnx", ".resx", ".json", ".yml", ".sh", ".py", ".toml", ".sql", ".mts", ".txt"];

    /// <summary>The key <see cref="ScannedFileCountsByRoot"/> uses for files at the repository root itself.</summary>
    private const string RootFilesKey = "<root files>";

    /// <summary>
    /// Ticket 260 slice 20 fix 1 — the EMPTY-WALK FLOOR, stated explicitly instead of resting on a
    /// single implicit canary. <see cref="RetiredSpellingsAreAbsentFromEveryScannedRoot"/> is green over
    /// an empty walk by construction; until this fix the only thing stopping that was the one exempt
    /// row still matching, which evaporates the day that exemption is discharged. Each row is a floor
    /// well under the tracked count at this pin, so ordinary deletions do not trip it and a walk that
    /// silently stopped reaching a root does.
    /// </summary>
    private static readonly (string Root, int MinimumFiles)[] WalkFloors =
    [
        ("packages", 1500), ("apps", 400), ("eng", 10), (RootFilesKey, 5),
        ("tooling", 3), ("src", 1), ("_shared", 1), ("tests", 1), (".github", 1), ("hosts", 1),
    ];

    /// <summary>
    /// The union of every extension any predecessor fence scanned and every one their review notes
    /// named as missing — i.e. every text extension tracked in this repository. A structural filter on
    /// the walk, not a per-row exception.
    /// </summary>
    private static readonly string[] ScannedExtensions =
    [
        ".cs", ".razor", ".csproj", ".slnx", ".props", ".targets", ".resx", ".md", ".json", ".yaml", ".yml",
        ".ts", ".mts", ".js", ".mjs", ".rs", ".css", ".tsv", ".txt", ".sh", ".py", ".toml", ".sql",
        ".editorconfig", ".gitattributes", ".gitignore", ".config",
    ];

    /// <summary>
    /// The ONE scan. Walks the whole repository — every root directory plus the repository-root files
    /// — filters by <see cref="ScannedExtensions"/>, skips <see cref="SkippedDirectories"/> at any
    /// depth, and matches identifier rows on an identifier boundary and literal rows as plain
    /// substrings, over the file's repository-relative PATH as well as its content.
    /// </summary>
    private static IEnumerable<(string Path, string Name)> Scan(string root)
    {
        var pattern = new Regex(
            @"(?<![A-Za-z0-9_])(" + string.Join('|', AllRetiredIdentifiers.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);

        return WalkTree(root, relativeDirectory: null)
            .Where(entry => ScannedExtensions.Any(extension => entry.Absolute.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(entry =>
            {
                var (path, relative) = entry;
                var haystack = relative + "\n" + File.ReadAllText(path);
                return RetiredLiterals
                    .Where(literal => haystack.Contains(literal, StringComparison.Ordinal))
                    .Concat(pattern.Matches(haystack).Select(match => match.Value))
                    .Select(match => (Path: relative, Name: match));
            })
            .Distinct();
    }

    /// <summary>
    /// The ONE walk both scans read: every file under <paramref name="relativeDirectory"/> (or the whole
    /// tree when it is null), with the repository-relative path, skipping <see cref="SkippedDirectories"/>
    /// at any depth. No extension filter — the callers apply their own, and the family-word scan applies
    /// none at all.
    /// </summary>
    private static IEnumerable<(string Absolute, string Relative)> WalkTree(string root, string? relativeDirectory)
    {
        var start = relativeDirectory is null
            ? root
            : Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(start)) return [];

        var separator = Path.DirectorySeparatorChar;
        var skipped = SkippedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
            .Select(path => (Absolute: path, Relative: Path.GetRelativePath(root, path).Replace(separator, '/')))
            // The skip test is on the path RELATIVE to the repository root, never the absolute one: a
            // checkout can itself sit under a skipped name (this repository's own worktrees live under
            // `.claude/worktrees/`), and an absolute-path test then silently skips the entire tree and
            // reports a green fence over nothing.
            .Where(entry => !entry.Relative.Split('/').SkipLast(1).Any(skipped.Contains));
    }

    /// <summary>
    /// The family-WORD scan slice 16 had and the consolidation lost: the retired F5 family word in ANY
    /// casing, in ANY file (no extension filter), in the content or in the repository-relative path.
    /// Applied only to <see cref="CleanDirectoryRows"/> — directories a slice has actually cleared.
    /// </summary>
    internal static IEnumerable<string> ScanTreeForFamilyWord(string root, string relativeDirectory) =>
        WalkTree(root, relativeDirectory)
            .Where(entry => (entry.Relative + "\n" + File.ReadAllText(entry.Absolute))
                .Contains(F5, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Relative)
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>Scanned files the whole-repository walk really visited, by root directory.</summary>
    internal static SortedDictionary<string, int> ScannedFileCountsByRoot()
    {
        var counts = WalkTree(RepositoryRoot(), relativeDirectory: null)
            .Where(entry => ScannedExtensions.Any(extension => entry.Absolute.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(entry => entry.Relative.Contains('/') ? entry.Relative[..entry.Relative.IndexOf('/')] : RootFilesKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new SortedDictionary<string, int>(counts, StringComparer.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
