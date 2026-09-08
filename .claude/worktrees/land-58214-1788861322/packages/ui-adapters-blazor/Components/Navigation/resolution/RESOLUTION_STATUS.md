---
component: HarborlineBreadcrumb, HarborlineBreadcrumbItem, HarborlineContextMenu, HarborlineEnvironmentBadge, HarborlineMenu, HarborlineMenuItem, HarborlinePagination, HarborlineTimeRangeSelector, HarborlineToolbar, HarborlineToolbarButton, HarborlineToolbarGroup, HarborlineToolbarSeparator, HarborlineToolbarToggleButton, HarborlineTreeItem, HarborlineTreeView
phase: 2
status: validated
complexity: mixed
priority: high
owner: ""
last-updated: 2026-04-02
depends-on: [HarborlineThemeProvider]
external-resources:
  - name: "Radzen Tree, MudBlazor TreeView, BlazorVirtualTreeView, Fancytree, excubo-ag, jsTree"
    url: "see Sources Evaluated table below"
    license: "MIT"
    approved: true
---

# Resolution Status: Navigation

## Current Phase
Phase 2: TreeView, Menu, ContextMenu, Pagination are Phase 2; Breadcrumb, Toolbar are Phase 3; remaining components are Phase 4

## Gap Summary
TreeView has 6 gaps (no expanded/selected binding), Menu has 7 gaps (hierarchy not wired), ContextMenu has 8 gaps (no selector/data binding), Breadcrumb 7 gaps, Toolbar 5 gaps, Pagination 6 gaps. Other components have minor gaps.

## Resolution Progress

### Completed
- [x] **HarborlinePagination** — IMPLEMENTED (6/6 gaps resolved): Added `Total`+`PageSize` model (auto-computes pages), renamed `CurrentPage`→`Page`, `MaxVisiblePages`→`ButtonCount`, added `PageSizes` dropdown, `PageSizeChanged` event, `ShowInfo` page info text. Updated all sample pages.

### Validated
- [x] **HarborlineTreeView** — 21/22 gaps RESOLVED, 1 DEFERRED (Gap 18 virtualization). Stage 06 validated 2026-04-02. Closure report: `stages/06-validate/output/gap-treeview-closure-report.md`. Follow-up: expand bUnit test coverage (4 tests → 50+ per TEST_PLAN.md), enhance demo page with advanced feature sections.

### In Progress
- [x] **HarborlineMenu** — IMPLEMENTED (5/7 gaps resolved): Refactored to partial files (.razor + .razor.cs), added `ItemTemplate` parameter, `ShowOn` parameter (MenuShowOn enum), full keyboard navigation (ArrowUp/Down, Enter/Space, Home/End, Escape). Deferred: popup collision settings, generic typing.
- [x] **HarborlineContextMenu** — IMPLEMENTED (6/8 gaps resolved): Refactored to partial files (.razor + .razor.cs), enhanced keyboard navigation (ArrowUp/Down/Left/Right, Enter/Space, Home/End), added `OnShow`/`OnHide` event callbacks. Deferred: Selector JS interop, popup collision, generic typing.

### Not Started
- [ ] HarborlineBreadcrumb — 7 gaps
- [ ] HarborlineToolbar — 5 gaps
- [ ] Minor components (BreadcrumbItem, EnvironmentBadge, MenuItem, ToolbarButton, etc.)

---

## HarborlineTreeView — Source Review Findings

### Sources Evaluated

| Source | Type | Key Value |
|--------|------|-----------|
| **Radzen Tree** | Blazor OSS | Tri-state checkbox propagation, SingleExpand, CheckedValues binding, TreeLevel data binding |
| **MudBlazor TreeView** | Blazor OSS | SelectionMode enum, ServerData lazy loading, FilterFunc, ExpandOnClick, AutoExpand, Dense mode |
| **BlazorVirtualTreeView** | Blazor OSS | Virtualization strategy for hierarchical data, programmatic navigation (SelectNodeAsync), dynamic node manipulation |
| **Fancytree** | JS OSS | Keyboard navigation, persistence, filtering/search, table/tree hybrid, extension system, conditional per-node options |
| **excubo-ag Blazor.TreeViews** | Blazor OSS | Minimal JS footprint, CheckboxTemplate delegate, RefreshSelection() explicit sync |
| **Fluent UI Blazor** | Blazor OSS | LazyLoadItems callback, SelectedItem binding, model-driven API |
| **jsTree** | JS OSS | Plugin architecture for feature modularity, checkbox/DnD/search/context-menu plugins, mass loading |
| **Syncfusion TreeView** | Commercial | Enterprise feature benchmark: lazy loading, checkbox mode, node editing, DnD, multi-select, keyboard nav |
| **Telerik TreeView** | Commercial | Direct parity target: checkboxes, DnD, bindings, lazy loading |

### Original 6 Gaps (IMPLEMENTED in code)

| # | Gap | Status | Implementation |
|---|-----|--------|----------------|
| 1 | ExpandedItems two-way binding | ✅ Done | `@bind-ExpandedItems` via `IEnumerable<string>` parameter + `ExpandedItemsChanged` |
| 2 | SelectedItems binding | ✅ Done | `@bind-SelectedItems` via `IEnumerable<string>` parameter + `SelectedItemsChanged` |
| 3 | Size parameter | ✅ Done | `Size` string parameter applies `font-size` style |
| 4 | Drag-and-drop | ✅ Done | `EnableDragDrop` + `OnItemDrop` event with HTML5 drag events |
| 5 | Rebind() method | ✅ Done | Public `Rebind()` calls `StateHasChanged()` |
| 6 | Item selection API | ✅ Done | `SelectItem()` clears + adds to `_selectedIds`, fires `SelectedItemsChanged` |

### New Gaps Identified from Source Review (16 gaps)

#### Priority 1 — Core Behavioral Gaps (ALL IMPLEMENTED)

| # | Gap | Status | Implementation |
|---|-----|--------|----------------|
| 7 | **Tri-state checkbox propagation** | ✅ Done | `AllowCheckChildren`/`AllowCheckParents` bools, `GetCheckState()` returns `bool?`, `UpdateAncestorCheckState()` recursive walk, `aria-checked="mixed"` for indeterminate |
| 8 | **CheckedItems two-way binding** | ✅ Done | `@bind-CheckedItems` via `IEnumerable<string>` + `CheckedItemsChanged`, synced in `OnParametersSet` |
| 9 | **Multi-selection mode** | ✅ Done | `TreeSelectionMode` enum (`None`/`Single`/`Multiple`), toggle behavior in Multiple mode, separate from checkbox state |
| 10 | **Lazy loading callback** | ✅ Done | `LoadChildrenAsync` `Func<object, Task<IEnumerable<object>>>`, load-once via `_loadedNodeIds` guard, loading indicator while async |
| 11 | **Keyboard navigation** | ✅ Done | WAI-ARIA TreeView: ArrowUp/Down, Left/Right expand/collapse, Enter/Space select+check, Home/End, * expand siblings, F2 edit |

#### Priority 2 — Enhanced Interaction Gaps (ALL IMPLEMENTED)

| # | Gap | Status | Implementation |
|---|-----|--------|----------------|
| 12 | **ExpandOnClick / ExpandOnDoubleClick** | ✅ Done | Bool parameters wired to header `onclick`/`ondblclick` → `ToggleNodeAsync` |
| 13 | **SingleExpand (accordion) mode** | ✅ Done | `SingleExpand` bool, `FindSiblingIds()` removes sibling expanded IDs before adding new |
| 14 | **AutoExpand to show selection** | ✅ Done | `AutoExpand` bool, `ExpandAncestorsOfSelected()` in `OnParametersSet` via `CollectAncestorIds()` |
| 15 | **ExpandAllAsync / CollapseAllAsync** | ✅ Done | Public async methods, `CollectAllIds()` recursive walk, fires `ExpandedItemsChanged` |
| 16 | **FilterFunc / Search** | ✅ Done | `Func<object, bool>` predicate, `ApplyFilter()` preserves ancestor chains, `ClearFilter()` public method, `.mar-tree-item--filter-match` CSS class |
| 17 | **Disabled / ReadOnly** | ✅ Done | Both parameters guard all interactions (click, drag, keyboard, checkboxes), `aria-disabled` on root |

#### Priority 3 — Advanced / Enterprise Gaps (4/5 IMPLEMENTED, 1 DEFERRED)

| # | Gap | Status | Implementation |
|---|-----|--------|----------------|
| 18 | **Virtualization** | ⏳ Deferred | Requires architectural change to flatten tree + `<Virtualize>` component. Deferred to future iteration — `GetVisibleNodeIds()` flattening is in place as a foundation. |
| 19 | **Programmatic navigation (SelectNodeAsync)** | ✅ Done | `SelectNodeAsync(string id)` expands all ancestors via `CollectAncestorIds()`, selects, sets focus, fires both binding callbacks |
| 20 | **ItemContextMenu event** | ✅ Done | `OnItemContextMenu` `EventCallback<TreeItemContextMenuEventArgs>`, wired via `@oncontextmenu` on header with `preventDefault` |
| 21 | **CheckboxTemplate** | ✅ Done | `RenderFragment<CheckboxContext>` parameter, `CheckboxContext` provides `Checked`, `Indeterminate`, `Disabled`, `OnChange` delegate |
| 22 | **Node editing (inline rename)** | ✅ Done | `AllowEditing` bool + `OnItemEdit` `EventCallback<TreeItemEditEventArgs>`, double-click or F2 activates, Enter commits, Escape cancels, blur commits |

### Implementation Summary

| Phase | Gaps | Status |
|-------|------|--------|
| **Phase 1 (Core)** | Gaps 7-11 | ✅ ALL IMPLEMENTED |
| **Phase 2 (Enhanced)** | Gaps 12-17 | ✅ ALL IMPLEMENTED |
| **Phase 3 (Advanced)** | Gaps 19-22 | ✅ 4/5 IMPLEMENTED |
| **Phase 3 (Deferred)** | Gap 18 (Virtualization) | ⏳ DEFERRED |

**Architecture:** Refactored to partial file pattern (`.razor` + `.razor.cs`) matching `HarborlineDataGrid` convention.
**New types added:** `TreeSelectionMode` enum, `TreeItemContextMenuEventArgs`, `TreeItemEditEventArgs`, `CheckboxContext` models.

## Blockers
- None
