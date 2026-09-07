# @harborline-software/ui-react — Component Master Catalog

- **Status:** Accepted
- **Version:** 1.0.0
- **Spec coverage:** ~1,020 contract files authored (corpus grew through Tier-A catalog reconciliation sweep 2026-06-06; 12 new A-series catalog rows A22–A33 added for catalog-missing components); adversarial hardening log + UPF meta-validation on file
- **Source baseline:** Telerik UI for Blazor / KendoReact (150 items) + OSS ecosystem gaps
- **Two tiers:**
  - `app-priority` — Harborline ERP app needs: `critical / high / medium / low / deferred`
  - `library-scope` — @harborline-software/ui-react long-term north-star: `v1 / planned / future-wave / out-of-scope`

---

## Purpose

This is the north-star catalog for `@harborline-software/ui-react`, the Harborline framework's shared React component library. It supersedes `M0-backlog.md` for **strategic planning purposes** — providing the full long-term vision across all categories and library-scope tiers.

`M0-backlog.md` remains the **operative extraction queue** (Top-20 committed, ~50 total with existing Blazor source specs). The M0-backlog Top-20 is a strict subset of the `v1` items in this catalog.

---

## Category Taxonomy

| Category | Description |
|---|---|
| DataDisplay | Grids, lists, trees, badges, avatars, pivot tables — components that render collections of data |
| DataEntry | All form inputs, pickers, selects, file upload — components that accept user input |
| DataVisualization | Charts, gauges, maps, sparklines — graphical representations of data |
| Scheduling | Calendar, scheduler, gantt, taskboard — time-oriented planning and display components |
| Overlays | Dialogs, drawers, sheets, popovers, tooltips, context menus — floating / layer-above-content UI |
| Navigation | AppBar, tabs, pager, breadcrumb, menu, treeview, stepper — wayfinding and structural navigation |
| Feedback | Notifications/toasts, loaders, progress, skeleton, state badges — communicating system status to users |
| Layout | Card, stack, grid-layout, splitter, tile, accordion, wizard — structural composition primitives |
| Media | Barcode, QR, PDF viewer, file manager, spreadsheet — rich media and document components |
| AI | AI prompt, chat, inline AI, speech-to-text, smart paste — AI-augmented interface components |
| Utility | Animation, drag/sortable, keyboard nav, ripple, export helpers, icons, typography — cross-cutting infrastructure |

**Taxonomy vs disk layout mismatch (DA3-13):** The catalog uses `Media` as a category, but the spec corpus splits Media items across several directories: `Feedback/` (PDFViewer, PDFEditor), `DataDisplay/` (Barcode, QRCode), `Layout/` (Map was misplaced before correction), and `DataVisualization/` (Sparkline). No `Media/` folder exists on disk. This mismatch is intentional — components are categorized in the catalog by functional domain while the disk layout groups them by implementation family. A future M2 taxonomy reorganization may align them. Until then, use the catalog `Category` column as authoritative and do NOT rely on directory names for category classification.

---

## Column Legend

| Column | Meaning |
|---|---|
| `#` | Sequence number (1–150 Telerik baseline; A1–A21 OSS additions) |
| `NormalizedName` | Canonical component name for `@harborline-software/ui-react` |
| `Category` | One of the 11 categories above |
| `app-priority` | Harborline ERP urgency: `critical / high / medium / low / deferred` |
| `library-scope` | @harborline-software/ui-react target release: `v1 / planned / future-wave / out-of-scope` |
| `spec-status` | `full` = 4 contracts authored; `stub` = 1-page out-of-scope or alias-redirect stub |
| `TelerikBlazor` | `✓` present in Telerik UI for Blazor; `—` absent |
| `KendoReact` | `✓` present in KendoReact; `—` absent |
| `MUI` | `✓` present in Material UI; `—` absent |
| `AntDesign` | `✓` present in Ant Design; `—` absent |
| `Vaadin` | `✓` present in Vaadin component set; `—` absent |
| `Radix/shadcn` | `✓` present in Radix UI primitives or shadcn/ui; `—` absent |
| `demo-story` | Storybook story present and verified in CI (HARBORLINE_API_A11Y_001 gate) |
| `visual-test` | Playwright visual regression baseline committed; screenshot generated in CI |
| `Notes` | Cross-library aliases, OSS primitive refs, or `source: <library>` for OSS-only additions |

---

## § Telerik / KendoReact Baseline (150 items)

| # | NormalizedName | Category | app-priority | library-scope | spec-status | TelerikBlazor | KendoReact | MUI | AntDesign | Vaadin | Radix/shadcn | demo-story | visual-test | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | ActionSheet | Overlays | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 2 | AI Prompt | AI | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 3 | Animation / AnimationContainer | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | |
| 4 | AppBar | Navigation | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 5 | Arc Gauge / ArcGauge | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 6 | Area Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 7 | AutoComplete | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | |
| 8 | Avatar | DataDisplay | low | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 9 | Badge | DataDisplay | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 10 | Bar Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 11 | Barcode | Media | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 12 | BottomNavigation | Navigation | low | planned | full | ✓ | ✓ | ✓ | — | — | — | — | — | |
| 13 | Box Plot | DataVisualization | deferred | future-wave | stub | ✓ | ✓ | — | — | — | — | — | — | no spec files authored — DA3-2/MG3-6 reconciliation |
| 14 | Breadcrumb | Navigation | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 15 | Bubble Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 16 | Bullet Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 17 | Button | DataEntry | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | |
| 18 | ButtonGroup | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | alias: ToggleGroup (Radix) |
| 19 | Calendar | Scheduling | deferred | future-wave | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 20 | Candlestick Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 21 | Card | Layout | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | shadcn Card |
| 22 | Carousel / ScrollView | Layout | low | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 23 | Chat | AI | deferred | future-wave | full | ✓ | ✓ | — | — | ✓ | — | — | — | Vaadin Message List |
| 24 | Checkbox | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Checkbox |
| 25 | Chip | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 26 | ChipList | DataEntry | low | planned | stub | ✓ | ✓ | — | — | — | — | — | — | contract authoring deferred |
| 27 | ChunkProgressBar | Feedback | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | alias of ProgressBar family |
| 28 | CircularGauge | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 29 | ColorGradient | DataEntry | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | sub-component of ColorPicker |
| 30 | ColorPalette | DataEntry | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | sub-component of ColorPicker |
| 31 | ColorPicker | DataEntry | low | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 32 | ComboBox | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Headless UI Combobox |
| 33 | Column Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 34 | ContextMenu | Overlays | low | planned | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | Radix DropdownMenu |
| 35 | DataGrid | DataDisplay | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 36 | DataQuery | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | headless utility only |
| 37 | DateInput | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | bare date input primitive |
| 38 | DatePicker | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 39 | DateRangePicker | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 40 | DateTimePicker | DataEntry | medium | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 41 | Date Math | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | utility helper, not a component |
| 42 | Diagram | Utility | deferred | out-of-scope | stub | ✓ | — | — | — | — | — | — | — | |
| 43 | Dialog | Overlays | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Dialog / Headless UI Dialog |
| 44 | DockManager | Layout | deferred | future-wave | full | ✓ | — | — | — | — | — | — | — | |
| 45 | Donut Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 46 | Drawer | Overlays | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | alias: Sheet (shadcn) |
| 47 | DropDownButton | DataEntry | medium | planned | full | ✓ | ✓ | — | ✓ | — | ✓ | — | — | Radix DropdownMenu trigger |
| 48 | DropDownList | DataEntry | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | alias: Select; Radix Select |
| 49 | DropDownTree | DataEntry | medium | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 50 | DropZone | Utility | medium | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 51 | Editor (Rich Text) | DataEntry | medium | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 52 | Error (label) | DataEntry | high | v1 | stub | ✓ | ✓ | — | — | — | — | — | — | sub-component of Form; Semantic-only alias stub |
| 53 | ExcelExport | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | |
| 54 | FieldArray | DataEntry | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 55 | FieldWrapper | DataEntry | high | v1 | full | ✓ | ✓ | — | — | — | — | — | — | complementary to FormField (no FormFieldContext); own Label/HintLabel/ErrorLabel |
| 56 | FileManager | Media | deferred | future-wave | full | ✓ | — | — | — | — | — | — | — | |
| 57 | FileSaver | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | |
| 58 | FileSelect | DataEntry | medium | v1 | full | ✓ | ✓ | — | ✓ | ✓ | — | — | — | alias: Upload |
| 59 | Filter | DataDisplay | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 60 | FloatingActionButton | Navigation | low | planned | full | ✓ | ✓ | ✓ | — | — | — | — | — | |
| 61 | FloatingLabel | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | sub-component of inputs |
| 62 | FlatColorPicker | DataEntry | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | sub-component of ColorPicker |
| 63 | Form | DataEntry | critical | v1 | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 64 | FormElement | DataEntry | critical | v1 | stub | ✓ | ✓ | — | — | — | — | — | — | alias: FormField wrapper |
| 65 | Gantt | Scheduling | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 66 | Gauges (group) | DataVisualization | deferred | future-wave | stub | ✓ | ✓ | — | — | — | — | — | — | umbrella row — individual gauge components specced separately (ArcGauge, LinearGauge, etc.) |
| 67 | GridLayout | Layout | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 68 | Heatmap Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 69 | Hint (label) | DataEntry | high | v1 | stub | ✓ | ✓ | — | — | — | — | — | — | sub-component of Form; Semantic-only alias stub |
| 70 | Icon / SvgIcon / FontIcon | Utility | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | infrastructure utility |
| 71 | InlineAIPrompt | AI | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 72 | Input | DataEntry | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | bare text input primitive |
| 73 | KeyboardNavigation | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | |
| 74 | Label | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Label |
| 75 | LinearGauge | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 76 | Line Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 77 | ListBox | DataDisplay | medium | planned | full | ✓ | ✓ | — | — | ✓ | — | — | — | |
| 78 | ListView | DataDisplay | medium | v1 | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 79 | Loader | Feedback | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 80 | LoaderContainer | Feedback | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | wraps Loader with overlay |
| 81 | Map | Media | deferred | future-wave | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 82 | MaskedTextBox | DataEntry | low | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 83 | MediaQuery | Utility | deferred | out-of-scope | stub | ✓ | ✓ | — | — | — | — | — | — | |
| 84 | Menu | Navigation | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix DropdownMenu/Menubar |
| 85 | MultiColumnComboBox | DataEntry | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 86 | MultiSelect | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 87 | MultiSelectTree | DataEntry | medium | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 88 | MultiViewCalendar | Scheduling | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 89 | Notification / Toast | Feedback | high | v1 | full | ✓ | ✓ | — | ✓ | — | ✓ | — | — | Radix Toast / shadcn Sonner |
| 90 | NumericTextBox | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 91 | OHLC Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 92 | OrgChart | DataVisualization | deferred | future-wave | full | ✓ | — | — | ✓ | — | — | — | — | |
| 93 | Pager | Navigation | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 94 | PanelBar | Layout | low | planned | full | ✓ | ✓ | — | ✓ | — | ✓ | — | — | **deprecated** — absorbed by Panel (mode="accordion") in Cohort 1 (2026-06-12); PanelBar is a passthrough shim |
| 95 | PDFGenerator | Media | deferred | future-wave | stub | ✓ | ✓ | — | — | — | — | — | — | no spec files authored — DA3-2/MG3-6 reconciliation |
| 96 | PDFViewer | Media | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 97 | PivotGrid | DataDisplay | deferred | future-wave | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 98 | Popover | Overlays | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | Radix Popover |
| 99 | Popup | Overlays | low | planned | full | ✓ | ✓ | ✓ | — | — | ✓ | — | — | low-level primitive; Radix primitive |
| 100 | ProgressBar | Feedback | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 101 | QRCode | Media | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | alias of Barcode family |
| 102 | Radar Area Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 103 | Radar Column Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 104 | Radar Line / Polar Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 105 | RadioGroup | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix RadioGroup |
| 106 | Range Area Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 107 | Range Bar Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 108 | Range Column Chart | DataVisualization | deferred | future-wave | stub | ✓ | ✓ | — | — | — | — | — | — | no spec files authored — DA3-2/MG3-6 reconciliation |
| 109 | RangeSlider | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 110 | Rating | DataEntry | low | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 111 | RichTextEditor | DataEntry | medium | planned | stub | ✓ | ✓ | — | ✓ | — | — | — | — | alias of Editor |
| 112 | Ripple | Utility | deferred | out-of-scope | stub | ✓ | ✓ | ✓ | — | — | — | — | — | UI effect only |
| 113 | Sankey Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 114 | Scheduler | Scheduling | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | recurring maintenance tasks; booking surface |
| 115 | ScrollView (Carousel) | Layout | low | planned | stub | ✓ | ✓ | — | — | ✓ | — | — | — | duplicate of #22 |
| 116 | SegmentedControl | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | alias: ToggleGroup (Radix) |
| 117 | Signature | DataEntry | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 118 | Skeleton | Feedback | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | shadcn Skeleton |
| 119 | Slider | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Slider |
| 120 | SmartPasteButton | AI | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 121 | Sortable | Utility | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 122 | Sparkline | DataVisualization | low | planned | full | ✓ | ✓ | — | ✓ | — | — | — | — | |
| 123 | SpeechToTextButton | AI | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 124 | SplitButton | DataEntry | medium | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 125 | Splitter | Layout | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 126 | Spreadsheet | Media | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 127 | StackLayout | Layout | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 128 | Stepper | Navigation | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | Canonical step-progress indicator. StepList + Steps consolidated here (2026-06-12, CIC KS-6 Q3). |
| 129 | StockChart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 130 | Switch | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Switch |
| 131 | TabStrip | Navigation | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Tabs |
| 132 | TaskBoard | Scheduling | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 133 | TextArea | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 134 | TextBox | DataEntry | critical | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 135 | TileLayout | Layout | low | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 136 | Timeline | DataDisplay | high | planned | full | ✓ | — | ✓ | ✓ | — | — | — | — | maintenance workflow history; work order log |
| 137 | TimePicker | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 138 | ToggleButton | DataEntry | low | planned | full | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | Radix Toggle |
| 139 | Toolbar | Navigation | high | v1 | full | ✓ | ✓ | ✓ | ✓ | — | — | — | — | |
| 140 | Tooltip | Overlays | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | Radix Tooltip |
| 141 | TreeList | DataDisplay | medium | v1 | full | ✓ | ✓ | — | — | ✓ | — | — | — | |
| 142 | TreeView | Navigation | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | |
| 143 | Typography | Utility | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | type scale utility |
| 144 | Upload | DataEntry | medium | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | alias: FileSelect |
| 145 | ValidationMessage | DataEntry | high | v1 | full | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — | sub-component of Form |
| 146 | ValidationSummary | DataEntry | high | v1 | full | ✓ | ✓ | — | — | — | — | — | — | sub-component of Form |
| 147 | ValidationTooltip | DataEntry | medium | planned | full | ✓ | ✓ | — | — | — | — | — | — | |
| 148 | Waterfall Chart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | |
| 149 | Window | Overlays | high | v1 | full | ✓ | ✓ | ✓ | — | ✓ | — | — | — | alias: Dialog (draggable variant) |
| 150 | Wizard | Navigation | medium | v1 | full | ✓ | ✓ | — | ✓ | — | — | — | — | |

---

## § OSS Ecosystem Additions (21 items)

Components not present in the Telerik / KendoReact baseline but filling OSS ecosystem gaps (Radix UI, shadcn/ui, Vaadin, Ant Design).

| # | NormalizedName | Category | app-priority | library-scope | spec-status | TelerikBlazor | KendoReact | MUI | AntDesign | Vaadin | Radix/shadcn | demo-story | visual-test | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A1 | CommandPalette / CommandMenu | Overlays | low | planned | full | — | — | — | ✓ | — | ✓ | — | — | shadcn Command; cmdk-based |
| A2 | HoverCard | Overlays | low | planned | full | — | — | — | — | — | ✓ | — | — | Radix HoverCard |
| A3 | Menubar | Navigation | medium | planned | full | — | — | — | ✓ | ✓ | ✓ | — | — | Radix Menubar; app-level menu bar |
| A4 | NavigationMenu | Navigation | medium | planned | full | — | — | — | — | — | ✓ | — | — | Radix Navigation Menu; header nav |
| A5 | Sheet / SidePanel | Overlays | medium | planned | full | — | — | ✓ | — | — | ✓ | — | — | shadcn Sheet; drawer variant |
| A6 | Sonner / ToastQueue | Feedback | medium | planned | full | — | — | — | — | — | ✓ | — | — | shadcn Sonner; stacked toast manager |
| A7 | Tour / GuidedWalkthrough | Utility | medium | planned | full | — | — | — | ✓ | — | — | — | — | Ant Design Tour |
| A8 | Transfer / ShuttleList | DataDisplay | low | planned | full | — | — | — | ✓ | — | — | — | — | Ant Design Transfer; source→target list |
| A9 | CRUDHelper / DataForm | DataEntry | medium | planned | full | — | — | — | — | ✓ | — | — | — | Vaadin CRUD; form + grid composition |
| A10 | AppLayout / Shell | Layout | high | v1 | stub | — | — | — | — | ✓ | — | — | — | Vaadin AppLayout; main shell wrapper |
| A11 | SideNav | Navigation | high | v1 | stub | — | — | — | — | ✓ | — | — | — | Vaadin SideNav; left-rail navigation |
| A12 | MasterDetail | DataDisplay | medium | planned | full | — | — | — | — | ✓ | — | — | — | Vaadin Details / expand-in-place pattern |
| A13 | Separator | Utility | low | planned | full | — | — | ✓ | ✓ | — | ✓ | — | — | Radix Separator; horizontal/vertical rule |
| A14 | ToggleGroup | DataEntry | medium | planned | full | — | — | — | ✓ | — | ✓ | — | — | Radix Toggle Group; multi-button exclusive/inclusive select |
| A15 | AspectRatio | Layout | low | planned | full | — | — | — | — | — | ✓ | — | — | Radix AspectRatio; responsive container |
| A16 | ScrollArea | Layout | low | planned | full | — | — | — | ✓ | — | ✓ | — | — | Radix ScrollArea; custom scrollbar |
| A17 | Collapsible | Layout | low | planned | full | — | — | — | — | — | ✓ | — | — | Radix Collapsible; headless expand/collapse; **preset="panel"** absorbs deprecated ExpansionPanel (Cohort 1 2026-06-12) |
| A18 | EmptyState | Feedback | high | v1 | stub | — | — | — | ✓ | — | ✓ | — | — | shadcn empty state; zero-data placeholder |
| A19 | PDFEditor | Feedback | medium | planned | full | — | — | — | — | — | — | — | — | Forward-spec; no reference implementation; planned for future wave |
| A20 | ESignatureField | DataEntry | medium | planned | full | — | — | — | — | — | — | — | — | Forward-spec; no reference implementation; planned for future wave |
| A21 | RoleGate | Utility | high | v1 | full | — | — | — | — | — | — | — | — | M1 implementation-first; auth/permission display-gating utility |
| A22 | Alert | Feedback | high | v1 | full | — | — | — | — | — | ✓ | — | — | shadcn Alert; status/callout message block; replaces deprecated CalloutBox |
| A23 | StatusBanner | Feedback | high | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; page-level status banner (info/warning/error/success tone) |
| A24 | Spinner | Feedback | high | v1 | full | — | — | — | — | — | — | — | — | shadcn/Radix; inline loading spinner; see also Loader (#79) for block overlay |
| A25 | ActionMenu | Navigation | medium | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; trigger button + dropdown list of contextual actions |
| A26 | DescriptionList | DataDisplay | medium | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; term/value definition list (dl/dt/dd semantic HTML wrapper) |
| A27 | CopyButton | DataEntry | low | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; clipboard copy utility button with feedback state |
| A28 | DataExportButton | DataEntry | medium | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; data export trigger button (CSV/Excel/PDF) |
| A29 | IconButton | DataEntry | medium | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; icon-only Button variant; alias of Button #17 with `size="icon"` |
| A30 | FunnelChart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | Forward-spec; no reference implementation; pipeline/conversion funnel visualization |
| A31 | PieChart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | Forward-spec; no reference implementation; pie/donut variant under Gauges umbrella |
| A32 | RadialGauge | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | Forward-spec; no reference implementation; falls under #66 Gauges group |
| A33 | ScatterChart | DataVisualization | deferred | future-wave | full | ✓ | ✓ | — | — | — | — | — | — | Forward-spec; no reference implementation; XY scatter plot |
| A34 | Callout | Feedback | low | planned | full | — | — | — | — | — | — | — | — | Harborline-native; inline callout/note block (info/warning/tip tones); lighter-weight than Alert A22 |
| A35 | CodeBlock | Feedback | low | planned | full | — | — | — | — | — | — | — | — | Harborline-native; syntax-highlighted code display block; wraps react-syntax-highlighter |
| A36 | MultiStepForm | DataEntry | low | planned | full | — | — | — | — | — | — | — | — | Harborline-native; multi-step form composition; simpler than Wizard #147 (no wizard chrome/nav) |
| A37 | NotificationCenter | Navigation | high | v1 | full | — | — | — | ✓ | — | — | — | — | Harborline-native; bell-icon dropdown panel listing pending proposals + recent results; grouped by kind (proposals/results/system); outside-click dismissal; lives in navigation/ family (#1244); app-wired (PR dedup) |
| A38 | ActivityLog | Navigation | medium | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; attributed history table/list (actor + action + timestamp); table and list layouts; generic log viewer; lives in navigation/ family (#1244); app-wired (PR dedup) |
| A39 | GuardedControl | Utility | high | v1 | full | — | — | — | — | — | — | — | — | Harborline-native; aircraft guarded-switch primitive — visible-but-covered, two-step arm/fire, spring-loaded re-cover; renders a resolved GuardComposition (§AD.2 composable-guard ruling); dogfood scope = cover preset (Build entry, publish, archive, pack install); added to catalog by CIC ruling 2026-07-06; lives in guards/ family |
| A40 | VirtualKeyboard | DataEntry | medium | planned | full | — | — | — | — | — | — | — | — | Harborline-native; on-screen tap keyboard over vendored declarative layout data (14-locale roster); explicitly NOT an IME — surfaces a localized advisory for zh/ja/vi; promoted from the `tooling/translation-review` prototype by CIC-approved catalog expansion 2026-07-18 (#2802); forward-spec, no reference implementation yet |

---

## § Catalog ↔ Spec corpus reconciliation (MG3-6 / DA3-2 sweep, 2026-06-06)

The spec corpus in `packages/ui-core/Contracts/` is broader than the 171-row Telerik/KendoReact-baseline + OSS-additions table above. Many components were specced during the Harborline ERP build wave before being slotted into the canonical catalog; others are deliberate "implementation-first" extensions (Harborline-native compositions, OSS forms-family wrappers, domain-bound components) that the wave-N extraction surfaced but the master catalog has not yet absorbed. The reconciliation here is the source-of-truth for "what's in the spec corpus that isn't on the table".

### 92 ghost specs — components in corpus, no direct catalog row

#### Tier A — alias of an existing catalog row (need catalog cross-link, 25 entries)

These specs claim a catalog row in their `Catalog row:` header. Most claims resolve correctly (the cross-reference script in this sweep simply didn't normalize the catalog's `/`-separated aliases the same way the spec did). The unresolved claims (catalog row number does not exist, or claim points to a different category) are flagged for follow-up.

| Spec | Family | Priority | Claimed catalog row | Resolution |
|---|---|---|---|---|
| TextField | DataEntry | critical | #134 TextBox / #72 Input | RESOLVED — TextField is the FormField-family wrapper; row #134 TextBox exists (DataEntry, critical, v1) |
| SelectField | DataEntry | critical | #48 DropDownList | RESOLVED — FormField-family wrapper over #48 DropDownList |
| FormField | DataEntry | critical | #55 FieldWrapper / #63 Form | RESOLVED — composition root over #55 FieldWrapper + #63 Form |
| Accordion | Layout | high | #94 PanelBar | RESOLVED — spec header updated to `#94 PanelBar (app-priority: low, library-scope: planned)` (2026-06-06) |
| Alert | Feedback | high | #A22 Alert | RESOLVED — new row A22 Alert added (2026-06-06); spec header updated |
| CheckboxField | DataEntry | high | #24 Checkbox | RESOLVED — FormField-family wrapper over #24 Checkbox |
| ComboBoxField | DataEntry | high | #32 ComboBox | RESOLVED — spec header updated to `#32 ComboBox (medium, v1)` (2026-06-06); ComboBoxField is FormField-family wrapper |
| DateField | DataEntry | high | #37 DateInput | RESOLVED — FormField-family wrapper over #37 DateInput |
| NumberField | DataEntry | high | #90 NumericTextBox | RESOLVED — FormField-family wrapper over #90 NumericTextBox |
| Pagination | Navigation | high | #93 Pagination | RESOLVED — #93 is "Pager" which is the canonical Harborline row; Pagination is the alias name; spec claim is correct in substance, mild copy nit (use "Pager" to match catalog vocab) |
| Spinner | Feedback | high | #A24 Spinner | RESOLVED — new row A24 Spinner added (2026-06-06); distinct from Loader #79 (block overlay); spec header updated to `#A24 Spinner` |
| StatusBanner | Feedback | high | #A23 StatusBanner | RESOLVED — new row A23 StatusBanner added (2026-06-06); spec header updated |
| SwitchField | DataEntry | high | #130 Switch | RESOLVED — spec header updated to `#130 Switch (medium, v1)` (2026-06-06); SwitchField is FormField-family wrapper |
| ActionMenu | Navigation | medium | #A25 ActionMenu | RESOLVED — new row A25 ActionMenu added (2026-06-06); Harborline-native; spec header updated |
| DataExportButton | DataEntry | medium | #A28 DataExportButton | RESOLVED — new row A28 DataExportButton added (2026-06-06); spec header updated |
| DescriptionList | DataDisplay | medium | #A26 DescriptionList | RESOLVED — new row A26 DescriptionList added (2026-06-06); spec header updated |
| ExpansionPanel | Layout | medium | #94 PanelBar | RESOLVED — spec header updated to `#94 PanelBar (low, planned)` as single-panel variant (2026-06-06); **Cohort 1 (2026-06-12)**: ExpansionPanel absorbed by Collapsible (preset="panel"); shim kept for one release |
| IconButton | DataEntry | medium | #A29 IconButton | RESOLVED — new row A29 IconButton added (2026-06-06); documented as icon-only Button #17 variant; spec header updated |
| Panel | Layout | medium | #94 PanelBar | RESOLVED — spec header updated to `#94 PanelBar (low, planned)` as simplified Panel variant (2026-06-06); **Cohort 1 (2026-06-12)**: Panel gains mode="accordion" (absorbs PanelBar) + resizable prop |
| TextAreaField | DataEntry | medium | #133 TextArea | RESOLVED — spec header updated to `#133 TextArea (medium, v1)` (2026-06-06) |
| CopyButton | DataEntry | low | #A27 CopyButton | RESOLVED — new row A27 CopyButton added (2026-06-06); spec header updated |
| FunnelChart | DataVisualization | low | #A30 FunnelChart | RESOLVED — new row A30 FunnelChart added (2026-06-06); spec header updated |
| PieChart | DataVisualization | low | #A31 PieChart | RESOLVED — new row A31 PieChart added (2026-06-06); spec header updated |
| RadialGauge | DataVisualization | low | #A32 RadialGauge | RESOLVED — new row A32 RadialGauge added (2026-06-06); under #66 Gauges umbrella; spec header updated |
| ScatterChart | DataVisualization | low | #A33 ScatterChart | RESOLVED — new row A33 ScatterChart added (2026-06-06); spec header updated |

#### Tier B — Harborline-native implementation-first extensions (64 entries)

These specs declare themselves "implementation-first" or "not in master catalog" in the spec header. They were extracted from the shipping `@harborline-software/ui-react` implementation during wave-N and are tracked through the spec corpus directly. The catalog SHOULD absorb them in a future amendment so that the catalog stays the canonical scope ground-truth.

| Family | Components |
|---|---|
| AI | PromptBox, ConversationList |
| DataDisplay | ColorDot, ColumnVisibilityMenu, CurrencyAmount, DescriptionList, FilterBar, FilterChips, Highlight, ListToolbar, SavedViewsMenu, SearchInput, SortControl, Tag |
| DataEntry | AddressForm, CalendarDayPicker, CreditCardField, CurrencyField, DocumentUploadZone, DurationField, FormSection, ImageUploader, InlineEdit, MonthYearPicker, NumberFormatField, NumberStepper, NumericInput, PercentageField, PhoneField, PinField, ReadonlyField, RecurrenceField, ScheduleField, SearchField, SearchableSelect, TagInput, ToggleSwitch |
| DataVisualization | Chart, ChartWizard, DrilldownChart, PyramidChart, RadarChart, Sankey, ScatterLineChart |
| Domain | PropertyCard, PropertySelector, RentIncreaseNotice, StatusPill |
| Feedback | ConnectionStatus, ErrorCard, FreshnessBadge, LoadingState, MaskedText, NotificationBell, NotificationDot, NumberBadge, OfflineIndicator, PDFExport, SyncStateBadge |
| Layout | (none) |
| Navigation | GlobalSearch, MegaMenu, NavDrawer, UserMenu |
| Overlays | ConfirmDialog |

#### Tier C — explicit "not-in-catalog" low-priority extras (3 entries)

| Spec | Family | Priority | Notes |
|---|---|---|---|
| Callout | Feedback | low | #A34 Callout | RESOLVED — new row A34 Callout added (2026-06-06); spec header updated |
| CodeBlock | Feedback | low | #A35 CodeBlock | RESOLVED — new row A35 CodeBlock added (2026-06-06); spec header updated |
| MultiStepForm | DataEntry | low | #A36 MultiStepForm | RESOLVED — new row A36 MultiStepForm added (2026-06-06); distinct from Wizard #147 (no wizard chrome); spec header updated |

### 4 ghost catalog rows — entries with no spec at all

These catalog rows are marked `stub` (was `full`, corrected in this sweep) because no spec files exist under the row's name or any alias.

| Row | Name | Family | Resolution |
|---|---|---|---|
| 13 | Box Plot | DataVisualization | stub — deferred / future-wave; spec deferred to future wave |
| 66 | Gauges (group) | DataVisualization | stub — umbrella row; canonical specs are per-gauge (ArcGauge, LinearGauge, RadialGauge if added) |
| 95 | PDFGenerator | Media | stub — deferred / future-wave; spec deferred to future wave |
| 108 | Range Column Chart | DataVisualization | stub — deferred / future-wave; spec deferred to future wave |

### Follow-up disposition

- **Tier A NEEDS-FIX rows** — RESOLVED 2026-06-06 in `feat/ui-core-catalog-tier-a-fixes`: stale `Catalog row:` headers fixed in 18 spec files; new A-series catalog rows A22–A33 added.
- **Tier C** — RESOLVED 2026-06-06: new A-series catalog rows A34 (Callout), A35 (CodeBlock), A36 (MultiStepForm) added; spec headers updated.
- **Tier B catalog absorption** (64 entries) — owner: PAO in a future contract-catalog amendment; either add as `§ Harborline-Native Implementation-First Components` section, or fold into the existing OSS Ecosystem Additions section. The spec corpus already treats them as canonical; the catalog is the lag indicator.
- **Tier C** (3 entries) — handle alongside Tier A NEEDS-FIX in the next amendment.
- **4 ghost catalog rows** — RESOLVED in this sweep (`spec-status: stub` + note). No further action unless the components advance from `future-wave` to a build wave.

---

## § Counts and Scope Summary

**Total catalog entries: 173** (150 Telerik/KendoReact baseline + 23 OSS additions)

v0.2.0 (2026-06-04): Timeline bumped high; Scheduler bumped medium/planned; demo-story + visual-test delivery columns added.
v0.2.1 (2026-06-20): +2 Harborline-native entries (A37 NotificationCenter, A38 ActivityLog) registered; PR #1298 stub dir retired — canonical impls live in navigation/ (#1244).

### By library-scope

| Scope | Count |
|---|---|
| `v1` | 59 |
| `planned` | 63 |
| `future-wave` | 42 |
| `out-of-scope` | 9 |

### By app-priority

| Priority | Count |
|---|---|
| `critical` | 8 |
| `high` | 28 |
| `medium` | 51 |
| `low` | 35 |
| `deferred` | 51 |

### By category

| Category | Count |
|---|---|
| DataDisplay | 12 |
| DataEntry | 54 |
| DataVisualization | 26 |
| Scheduling | 5 |
| Overlays | 11 |
| Navigation | 14 |
| Feedback | 10 |
| Layout | 13 |
| Media | 7 |
| AI | 5 |
| Utility | 16 |

---

## § Relationship to M0-backlog.md

`M0-backlog.md` is the **operative extraction queue** for the current build wave: ~20 committed Top-20 items with existing Blazor source specs ready for React extraction, and a further ~30 queued items in priority order.

This catalog is the **long-term vision** — the full north-star scope that `@harborline-software/ui-react` will grow toward over multiple release waves.

**The M0-backlog Top-20 is a strict subset of the `v1` items in this catalog.** Anything that enters the operative queue should already appear here with `library-scope: v1`; if it does not, this catalog should be amended first.

As waves complete and M0-backlog items ship, their status in this catalog advances from `v1` (planned) to shipped (tracked in CHANGELOG.md). The operative queue advances through `planned` and `future-wave` entries in subsequent waves.
