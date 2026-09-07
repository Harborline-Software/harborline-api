# Phase M0 — Component Spec-Extraction Backlog

- **Status:** Accepted
- **Phase:** ADR 0017-A1 Phase M0 (earlier repository ticket #260)
- **Source inventory:** `apps/docs/component-specs/` (Blazor-era `SunfishXxx` specs) +
  `apps/docs/component-specs/component-mapping.json`
- **Owner:** Engineer (Semantic + Interaction extraction); PAO (Styling + Accessibility)
- **Operative catalog:** `component-master-catalog.md` v1.0.0 (Status: Accepted; see catalog header for current spec-file count — stale counts in this file are non-authoritative; catalog is ground truth) — supersedes this backlog for strategic scope decisions; this file remains the operative extraction queue for build-wave prioritization

---

## Reconciliation status (2026-06-06)

**DA3-12 reconciliation against the ~1,020-file corpus (post-PR-989/PR-1001):**

All Top-20 M0 components and all v1-tier backlog components now have full 4-contract sets (`Status: Accepted`). The extraction queue is functionally complete for the M0 scope.

**Name-mapping clarifications** — backlog names that map to catalog canonical names:

| Backlog name | Canonical catalog name | Catalog row | Status |
|---|---|---|---|
| AutocompleteField | AutoComplete | #7 | Full — 4 contracts |
| DateRangeField | DateRangePicker | #39 | Full — 4 contracts |
| FileField | Upload / FileSelect | #144 / #58 | Full — 4 contracts each |
| MultiSelectField | MultiSelect | #86 | Full — 4 contracts |
| Stack | StackLayout | #127 | Full — 4 contracts |

**ONR R9 additions not yet in catalog** — three additions proposed in §3 (from cross-source survey R9) have no catalog rows and no contracts; they are pending M2 catalog editorial admission:

| Component | Backlog §3 family | Block |
|---|---|---|
| Statistic | DataDisplay | No catalog row; scope/priority TBD by council |
| Descriptions | DataDisplay | No catalog row; scope/priority TBD by council |
| Result | Feedback | No catalog row; scope/priority TBD by council |

**Out-of-M0-scope items (§4) now spec'd** — the charts, scheduling, AI, and media waves listed as "Out of M0 scope" in §4 were covered in Phases 2–3 of the master plan (`idempotent-dazzling-bee.md`). All have full 4-contract forward-specs in `DataVisualization/`, `Scheduling/`, `AI/`, and `Media/` subdirectories. The §4 deferral note reflects M0 scope only, not overall spec status.

---

## 0. Naming convention

All `@harborline-software/ui-react` component exports use **clean functional names with no package prefix**. The import path itself (`@harborline-software/ui-react`) is the namespace; prefixing at the component level is redundant and inconsistent with shadcn/Radix conventions.

**Rules:**
- No library prefix (e.g. `Button`, not `<Library>Button`).
- Input/form controls that wrap a native input and compose with `FormField` use an `XxxField` suffix: `TextField`, `SelectField`, `CheckboxField`, `NumberField`, `DateField`, `TextAreaField`, `RadioGroupField`, `SwitchField`, etc.
- Layout, display, overlay, navigation, and feedback components use clean functional names with no suffix: `Button`, `Badge`, `Card`, `Dialog`, `Drawer`, `Loader`, `Notification`, `AppBar`, `TabStrip`, `Pager`, `Toolbar`, etc.
- Blazor-era `SunfishXxx` identifiers, and the Blazor adapter's own `HarborlineXxx` type names, appear only in the **Blazor spec** column of this backlog as source references — they are never public API names.

---

## 1. Purpose

This backlog is the priority-ordered worklist for extracting framework-neutral
contracts (Semantic / Interaction / Styling / Accessibility) from the existing
Blazor-era component specs into `packages/ui-core/Contracts/`. DataGrid is the
first extraction (this PR); this backlog sequences the rest.

**Priority is set against the Harborline MVP** — a property-management / ERP application.
Its critical path is **data tables + CRUD entry forms + modal dialogs + action
feedback**, so those families lead. `critical` = MVP cannot ship without it;
`high` = MVP needs it for a complete first release; `medium` = expected soon after;
`low` = nice-to-have / later wave.

**Family assignment methodology.** Each component is assigned exactly one of the six
ADR 0017 families (DataDisplay / Feedback / Forms / Overlays / Navigation / Layout) by
component **semantics**. Where that differs from the `category` field in
`component-mapping.json`, the mapping category is shown in parentheses so the council
can reconcile. Out-of-the-six-families categories from the mapping (`charts`,
`scheduling`, `media`, `ai`, `utility`) are **out of M0 scope** — see §4.

---

## 2. Top-20 priority-ordered (the M0 extraction front)

| # | Component | ADR 0017 family | MVP priority | Blazor spec |
| --- | --- | --- | --- | --- |
| 1 | DataGrid | DataDisplay | critical | `grid/` |
| 2 | Button | Forms | critical | `button/` |
| 3 | TextField | Forms | critical | `textbox/` |
| 4 | SelectField | Forms | critical | `dropdownlist/` |
| 5 | Form | Forms | critical | `form/` |
| 6 | Pager | Navigation | critical | `pager/` |
| 7 | ValidationSummary | Forms | high | `validation/` |
| 8 | CheckboxField | Forms | high | `checkbox/` |
| 9 | NumberField | Forms | high | `numerictextbox/` |
| 10 | DateField | Forms | high | `datepicker/` |
| 11 | Dialog | Overlays | high | `dialog/` |
| 12 | Notification | Feedback | high | `notification/` |
| 13 | Loader | Feedback | high | `loader/` |
| 14 | Toolbar | Navigation | high | `toolbar/` |
| 15 | TabStrip | Navigation _(mapping: layout)_ | high | `tabstrip/` |
| 16 | AppBar | Layout | high | `appbar/` |
| 17 | Card | Layout | high | `card/` |
| 18 | Drawer | Overlays _(mapping: layout)_ | high | `drawer/` |
| 19 | Badge | DataDisplay | high | `badge/` |
| 20 | TextAreaField | Forms | medium | `textarea/` |

**Extraction sequence note:** items 1, 6 (Grid + Pager) compose directly (Pager is
rendered by Grid when `pageable`), so extracting them together is efficient. Items
2–5 are the form primitives the whole app depends on; they should follow immediately.

---

## 3. Full backlog grouped by ADR 0017 family

Ordered within each family by MVP priority.

### DataDisplay

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| DataGrid | critical | `grid/` | M0 anchor (this PR). |
| Badge | high | `badge/` | Status pills throughout the ERP. |
| ListView | medium | `listview/` | Card/list alt to grid on small screens. |
| TreeList | medium | `treelist/` | Hierarchical data (chart-of-accounts, property tree). |
| Statistic | medium | — | Dashboard KPI tiles (metric + label); present in Ant Design + MUI as a standard DataDisplay primitive. _(added ONR cross-source survey R9)_ |
| Descriptions | medium | — | Read-only field-value display (key: value grid); Ant Design pattern for detail views without a full DataGrid. _(added ONR R9)_ |
| Sortable | medium | — | Reorderable line items / column ordering via drag-and-drop; extension of DataGrid's column-reorder story. _(added ONR R9)_ |
| Avatar | low | `avatar/` | User/tenant glyphs. |

### Forms

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| Button | critical | `button/` | Action primitive (mapping category: buttons). |
| TextField | critical | `textbox/` | Core text input. |
| SelectField | critical | `dropdownlist/` | Single-select dropdown. |
| Form | critical | `form/` | Form layout + binding host. |
| ValidationSummary | high | `validation/` | Field/form validation surface. |
| CheckboxField | high | `checkbox/` | Boolean input. |
| NumberField | high | `numerictextbox/` | Currency / quantity entry. |
| DateField | high | `datepicker/` | Dates pervade ERP records. |
| TextAreaField | medium | `textarea/` | Multiline notes. |
| RadioGroupField | medium | `radiogroup/` | Single choice from a small set. |
| SwitchField | high | `switch/` | Toggle setting. Feature-flag + preference toggles pervade ERP settings. _(bumped from `medium` per ONR cross-source survey R8)_ |
| DateRangeField | medium | `daterangepicker/` | Reporting-period filters. |
| MultiSelectField | medium | `multiselect/` | Multi-value selection. |
| ComboBoxField | high | `combobox/` | Editable select / type-ahead lookup. Universal ERP primitive — present in Ant Design, MUI, KendoReact; every lookup field uses it. _(bumped from `medium` per ONR cross-source survey R8)_ |
| AutocompleteField | medium | `autocomplete/` | Type-ahead lookup. |
| FileField | medium | `upload/` | Document attachments (mapping: forms; alias of `fileselect`). |
| ButtonGroup | low | `buttongroup/` | Segmented actions (mapping: buttons). |
| Chip | low | `chip/` | Removable tags (mapping: buttons). |
| Slider | low | `slider/` | Range input. |
| Rating | low | `rating/` | Star rating. |
| MaskedField | low | `maskedtextbox/` | Pattern-constrained input. |
| TimeField | low | `timepicker/` | Time-of-day entry. |
| ColorField | low | `colorpicker/` | Theme/label color. |
| ToggleGroup | medium | — | Exclusive + multi-select filter chip groups; Radix UI primitive; segmented-button UX for filter rows. _(added ONR cross-source survey R9)_ |

### Overlays

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| Dialog | high | `dialog/` | Modal CRUD / confirm (also `window/`). |
| Drawer | high | `drawer/` | Side panel / nav (mapping: layout). |
| Popover | medium | `popover/` | Anchored transient panel (mapping: data-display). |
| Tooltip | medium | `tooltip/` | Hover/focus hint (mapping: data-display). |
| ContextMenu | low | `contextmenu/` | Right-click actions (mapping: navigation). |
| Popup | low | `popup/` | Low-level anchored popup primitive (mapping: utility). |
| HoverCard | medium | — | Entity-link preview panel on hover/focus; Radix UI primitive; rich link previews for property/tenant/invoice references. _(added ONR cross-source survey R9)_ |

### Navigation

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| Pager | critical | `pager/` | Composes with Grid; 1-based paging. |
| Toolbar | high | `toolbar/` | Hosts ListToolbar filter/search/actions above grids. |
| TabStrip | high | `tabstrip/` | Panel navigation (mapping: layout). |
| Breadcrumb | medium | `breadcrumb/` | Location trail. |
| Menu | medium | `menu/` | App menu bar. |
| TreeView | medium | `treeview/` | Nav tree. |

### Feedback

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| Notification | high | `notification/` | Transient action feedback. |
| Loader | high | `loader/` | Busy indicator (also `loadercontainer/`). |
| Alert | high | — | Persistent page-level alert banner (distinct from Notification toast + StatusBanner inline). Ant Design `Alert` + MUI `Alert` pattern: `info / success / warning / error` variants, optional close button, optional action. Council to confirm scope vs existing StatusBanner. _(added ONR cross-source survey R9)_ |
| ProgressBar | medium | `progressbar/` | Determinate progress (also `chunkprogressbar/`). |
| Skeleton | medium | `skeleton/` | Loading placeholder; pairs with Grid loading state. |
| Result | low | — | 404 / no-permission / empty / success status pages; Ant Design `Result` pattern for full-page status surfaces. _(added ONR R9)_ |

### Layout

| Component | MVP priority | Blazor spec | Notes |
| --- | --- | --- | --- |
| AppBar | high | `appbar/` | Top app shell. |
| Card | high | `card/` | Content container. |
| Stack | medium | `stacklayout/` | Flow layout primitive. |
| GridLayout | medium | `gridlayout/` | CSS-grid layout primitive. |
| Stepper | high | `stepper/` | Multi-step flows (mapping: layout). Onboarding + multi-step wizards; universal across Ant Design, MUI, Telerik. _(bumped from `medium` per ONR cross-source survey R8)_ |
| Wizard | medium | `wizard/` | Guided multi-step (onboarding). |
| Accordion | high | `panelbar/` | Collapsible sections. Settings panels + FAQ-style detail pages; universal across all surveyed design systems. _(bumped from `low` per ONR cross-source survey R8)_ |
| Splitter | low | `splitter/` | Resizable panes. |
| TileLayout | low | `tilelayout/` | Dashboard tiles. |

---

## 4. Out of M0 scope

These mapping-category groups do not map to the six ADR 0017 M0 families and are
deferred to later waves (or are infrastructure rather than user-facing components):

- **charts** — chart, stockchart, sankey, gauges, pivotgrid (data-viz wave).
- **scheduling** — calendar, scheduler, gantt (scheduling wave; several `planned`/no Harborline component yet).
- **media** — barcodes, map, pdfviewer (media wave).
- **ai** — aiprompt, inlineaiprompt, chat, promptbox, smartpastebutton, speechtotextbutton (AI wave).
- **utility / infrastructure** — animationcontainer, diagram, dropzone, mediaquery,
  rootcomponent (ThemeProvider), spreadsheet, filemanager — mostly infra or specialized;
  re-triage individually in a later wave.
- **planned, no Harborline component yet** — `filter`, `gantt`, `scheduler`, `signature`,
  `multicolumncombobox` (no Blazor-era source entry in the mapping) — no source spec to extract from yet.

---

## 5. Notes for the council

1. **Family reconciliation:** the parenthetical "(mapping: …)" markers flag the 6
   components where this backlog's semantic family assignment differs from
   `component-mapping.json`'s `category`. The council should ratify the canonical family
   for TabStrip, Drawer, Popover, Tooltip, ContextMenu, and ButtonGroup.
2. **Button has no dedicated family:** the mapping uses a `buttons` category that ADR 0017
   does not enumerate. This backlog folds buttons into **Forms** (interactive controls).
   Confirm, or add an Actions family.
3. **Aliased specs:** several Blazor specs alias one Harborline component (e.g.
   `colorgradient`/`colorpalette`/`flatcolorpicker` → ColorField;
   `loader`/`loadercontainer` → Loader; `upload`/`fileselect` → FileField;
   `dialog`/`window` → Dialog). Extract once per Harborline component, not per alias.
4. **Top-20 is the committed M0 front;** items beyond it are sequenced but not committed
   until the council confirms this priority order against the live MVP backlog.
5. **ONR cross-source priority bumps (R8):** ComboBoxField, SwitchField, Stepper, and
   Accordion have been bumped to `high` based on the 15-source cross-system survey
   (the earlier repository's `_shared/research/component-survey-cross-source-2026-06-04.md`). Council
   to confirm or revert vs the live MVP timeline.
6. **ONR new components (R9):** Alert, Statistic, Descriptions, HoverCard, ToggleGroup,
   Sortable, and Result have been added from the cross-source survey. These have no
   Blazor-era source spec. **Alert** in particular needs council clarification on scope
   vs existing StatusBanner (inline banner) and Notification (toast) — the three
   components serve related but distinct use cases.
7. **ONR deferred API-level amendments requiring council ratification:**
   - **R3** — Controlled vs uncontrolled triple (`value` / `defaultValue` / `onChange`):
     should contracts add `defaultValue` for uncontrolled mode? Currently controlled-only.
   - **R4** — `asChild` on overlay trigger sub-components (Dialog.Trigger, Popover.Trigger,
     etc.): adopt Radix Slot pattern on trigger primitives?
   - **R6** — `startIcon` / `endIcon` naming vs current `leadingIcon` / `trailingIcon` in
     Button: MUI/Ant Design converge on `start`/`end`; Telerik uses a single `Icon` param.
   - **R10** — Codify `htmlElement` metadata field in each Semantic contract header.
   - **R11** — Two-axis className customization: `className` (root) + `classNames` object
     (per-region) on all composite primitives.
   Full details for each in `_shared/research/component-survey-cross-source-2026-06-04.md`.
