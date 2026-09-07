# Toolbar — Semantic Contract

- **Component:** Toolbar
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** Interaction (PAO) · [Styling](./Toolbar.Styling.md) · [Accessibility](./Toolbar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Toolbar.tsx`
- **Catalog row:** #139 Toolbar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` toolbar wrapper

---

## 1. Purpose

Toolbar is the canonical **action + filter row** primitive of
`@harborline-software/ui-react`. It renders a horizontal strip of controls above or
alongside a content area — typically a DataGrid — housing:

- **Primary actions** (Create, Import, Export) on the leading edge.
- **Search / filter controls** (search input, filter chips, status dropdowns)
  in the centre.
- **Secondary / contextual actions** (column visibility, bulk-action menu) on
  the trailing edge.

Toolbar is a **structural layout primitive** — it provides three named regions
with appropriate alignment and spacing, but has no internal state, no event
surface, and no knowledge of the content area below it. The host populates each
region with arbitrary ReactNode content.

This is the "ListToolbar" pattern used above every DataGrid page in the
Harborline MVP: it is the most common Toolbar composition.

---

## 2. Data model

Toolbar has no internal data model. It renders three flex regions.

```typescript
interface ToolbarProps extends React.HTMLAttributes<HTMLDivElement> {
  leading?: React.ReactNode
  trailing?: React.ReactNode
  children?: React.ReactNode
  size?: 'sm' | 'md'
  border?: 'none' | 'bottom'
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `leading` | `ReactNode` | — | Content anchored to the left edge of the Toolbar. Typical: a `Button variant="primary"` ("New invoice"), an icon-only group of action buttons. |
| `trailing` | `ReactNode` | — | Content anchored to the right edge of the Toolbar. Typical: secondary actions (Settings icon, column visibility toggle, export button). |
| `children` | `ReactNode` | — | Content in the centre of the Toolbar (or filling remaining space when `leading`/`trailing` are absent). Typical: a search input, a filter row, a segmented filter TabStrip. |
| `size` | `'sm' \| 'md'` | `'md'` | Height and inter-element padding. `'md'` is the standard density for most list pages; `'sm'` for compact/embedded toolbars. PAO Styling owns the tokens. |
| `border` | `'none' \| 'bottom'` | `'none'` | Optional bottom border to separate the Toolbar from the content area below. `'bottom'` is useful when the Toolbar sits inside a Card without padding. |
| HTML attributes | — | — | Spread onto the root `<div>`. |

### 3.1 Three-region layout

```
┌──────────────────────────────────────────────────────────────┐
│  [leading]          [children (centre)]         [trailing]   │
└──────────────────────────────────────────────────────────────┘
```

- **Leading region:** `flex shrink-0` — does not grow; anchored to the left.
- **Centre region:** `flex-1` — grows to fill available space. When no `children`
  is supplied, the centre is empty and leading + trailing are pushed to the
  respective edges.
- **Trailing region:** `flex shrink-0` — does not grow; anchored to the right.

All three regions are optional. Toolbar renders fine with any subset populated.

### 3.2 All-props-optional design

All three content props are optional. Valid single-region Toolbars:

```tsx
{/* Leading only — create action */}
<Toolbar leading={<Button variant="primary">New invoice</Button>} />

{/* Trailing only — secondary menu */}
<Toolbar trailing={<Button size="icon" aria-label="Column settings">…</Button>} />

{/* Centre only — search bar */}
<Toolbar>
  <TextField name="search" placeholder="Search invoices…" value={q} onChange={setQ} />
</Toolbar>
```

### 3.3 HTML attribute passthrough

Toolbar spreads HTML attributes onto the root `<div>`. Supports `id`, `className`,
`data-*`, `aria-*`, `role` etc.

### 3.4 Semantic role

Toolbar renders a plain `<div>` by default. When the content is genuinely
a toolbar of interactive controls, the host should add `role="toolbar"` via
HTML attribute passthrough and manage keyboard focus with `aria-label`.
PAO Accessibility owns the exact ARIA guidance for the toolbar pattern.

---

## 4. Events — semantics

Toolbar has **no events**. It is a layout container; all interaction events
belong to the controls inside it.

---

## 5. Slots

| Slot prop | Purpose | Alignment |
| --- | --- | --- |
| `leading` | Primary actions / create buttons | Left edge, shrink-0 |
| `children` | Search / filter controls | Centre, flex-1 |
| `trailing` | Secondary actions / settings | Right edge, shrink-0 |

All three are optional ReactNode; hosts compose freely within each region.

---

## 6. Component composition

### Standard ListToolbar (most common pattern)

The canonical "list page" composition above a DataGrid:

```tsx
<Toolbar
  leading={
    <Button variant="primary" onClick={() => nav('/invoices/new')}>
      New invoice
    </Button>
  }
  trailing={
    <Button size="icon" variant="ghost" aria-label="Column settings">
      <Settings2Icon className="h-4 w-4" />
    </Button>
  }
>
  <TextField
    name="search"
    placeholder="Search invoices…"
    value={searchQuery}
    onChange={setSearchQuery}
    size="sm"
  />
</Toolbar>
<DataGrid data={invoices} columns={columns} />
```

### Filter strip with SelectField

```tsx
<Toolbar>
  <div className="flex gap-2">
    <SelectField name="status" value={statusFilter} onValueChange={setStatusFilter} placeholder="All statuses">
      {statusOptions.map(s => <SelectOption key={s.value} value={s.value}>{s.label}</SelectOption>)}
    </SelectField>
    <DateField name="dateFrom" value={dateFrom} onChange={setDateFrom} placeholder="From" size="sm" />
    <DateField name="dateTo" value={dateTo} onChange={setDateTo} placeholder="To" size="sm" />
  </div>
</Toolbar>
```

### Toolbar inside a Card (with bottom border)

```tsx
<Card padding="none">
  <CardHeader>
    <CardTitle>Invoices</CardTitle>
  </CardHeader>
  <Toolbar border="bottom" leading={<Button variant="primary">New</Button>}>
    <TextField name="q" value={q} onChange={setQ} placeholder="Search…" />
  </Toolbar>
  <CardContent>
    <DataGrid … />
  </CardContent>
</Card>
```

### Contextual bulk-action toolbar

Some designs show a secondary Toolbar (replacing or supplementing the main one)
when DataGrid rows are selected:

```tsx
{selectedRows.length > 0 && (
  <Toolbar
    leading={
      <>
        <span className="text-sm text-muted-foreground">{selectedRows.length} selected</span>
        <Button variant="destructive" size="sm" onClick={handleBulkDelete}>Delete</Button>
      </>
    }
    trailing={
      <Button variant="ghost" size="sm" onClick={() => setSelectedRows([])}>Clear</Button>
    }
  />
)}
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Overflow handling** — when the Toolbar's controls exceed available width,
  a "More" overflow menu collapses the trailing items. Deferred.
- **Sticky / floating mode** — Toolbar pinned to the top of the viewport as
  the user scrolls the content below. Hosts apply Tailwind `sticky top-0`
  externally.
- **Dividers between regions** — explicit `border-r` dividers between leading /
  centre / trailing. Hosts add via `leading` / `trailing` content layout.
- **Collapsed / mobile mode** — automatic collapse to an icon row or hamburger
  on narrow viewports. Hosts manage breakpoint logic externally.
- **`role="toolbar"` default** — the semantic `role="toolbar"` is not set by
  default because not all Toolbar usages are technically toolbar regions (some
  are filter rows, not action bars). Deferred to PAO Accessibility guidance.

---

## 8. Open questions (for council)

1. **`leading` / `trailing` vs named sub-components.** This contract uses slot
   props (`leading`, `trailing`). An alternative is dot-notation sub-components
   (`<Toolbar.Leading>`, `<Toolbar.Trailing>`). Leaning slot props — Toolbar is
   a flat layout container, not a composable multi-region component; slot props
   are simpler.
2. **Default `role`.** Should Toolbar set `role="toolbar"` by default? The ARIA
   toolbar pattern requires `aria-label`. Leaning **no** — not all Toolbar
   usages are action toolbars (some are filter rows); defaulting to
   `role="toolbar"` would require the host to always supply `aria-label`.
   Document as opt-in via HTML attribute passthrough.
3. **`border="bottom"` default.** Should the bottom border be on by default
   when Toolbar is rendered inside a Card? The component has no way to detect
   this context; the host must opt in. Leaning keep `none` default.
4. **Toolbar + DataGrid spacing.** Should the vertical gap between Toolbar and
   the DataGrid below be owned by Toolbar (via `mb-*` margin) or by the
   parent layout? Leaning parent layout — Toolbar should not assume it's above
   a DataGrid.
5. **`size="sm"` use case.** When is a compact toolbar needed? Primarily for
   embedded/side-panel contexts. Confirm the two-size taxonomy covers the
   Harborline use cases.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/navigation/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
