# TabStrip — Semantic Contract

- **Component:** TabStrip
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TabStrip.Interaction.md) · [Styling](./TabStrip.Styling.md) · [Accessibility](./TabStrip.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/TabStrip.tsx`
- **Catalog row:** #131 TabStrip (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Tabs`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** Radix UI `@radix-ui/react-tabs` (`Tabs`, `TabsList`, `TabsTrigger`, `TabsContent`)

---

## 1. Purpose

TabStrip is the canonical **tab navigation** primitive of `@harborline-software/ui-react`.
It groups related panels of content behind a row of tab triggers; activating
a trigger reveals the matching panel and hides the others. Typical use:

- Sub-navigation within a page (e.g. Property detail page: Overview / Leases
  / Work Orders / Documents).
- Form sections (e.g. Settings: Profile / Notifications / Security).
- Filtering / view-switching (e.g. Invoices: All / Outstanding / Paid).

This is a **forward-spec**: TabStrip has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the Radix UI Tabs primitive, adapted to Harborline's variant +
orientation vocabulary.

TabStrip is **composable** — like Card and Dialog, it exposes named
sub-components (TabList / Tab / TabPanel) so hosts can interleave content
naturally rather than declaring tabs as data.

> **⚠ Critical known gap (G-TS1) — Tab overflow:** When tabs exceed the available container width, they overflow horizontally with no scroll affordance or "more" menu. This is the dominant failure mode for property-detail pages with 5–6 tabs (Overview / Leases / Work Orders / Documents / History / Notes) on mobile viewports. There is no host-side workaround in M2 that is not janky. Keep tab count ≤ 4 on mobile-first pages until the overflow (`OverflowMode: Scroll | Menu`) is addressed in M2.1. See §7 for the deferred roadmap.

---

## 2. Data model

TabStrip's data model is declarative-composition (not data-driven):

```typescript
type TabStripOrientation = 'horizontal' | 'vertical'
type TabStripVariant = 'underline' | 'pill' | 'card'
type TabActivationMode = 'automatic' | 'manual'

interface TabStripProps {
  value: string
  onValueChange: (value: string) => void
  orientation?: TabStripOrientation
  variant?: TabStripVariant
  activationMode?: TabActivationMode
  children: React.ReactNode
}

interface TabListProps extends React.HTMLAttributes<HTMLDivElement> {
  children: React.ReactNode
}

interface TabProps extends React.HTMLAttributes<HTMLButtonElement> {
  value: string
  disabled?: boolean
  badge?: React.ReactNode
  children: React.ReactNode
}

interface TabPanelProps extends React.HTMLAttributes<HTMLDivElement> {
  value: string
  forceMount?: boolean
  children: React.ReactNode
}
```

A canonical composition:

```tsx
<TabStrip value={view} onValueChange={setView}>
  <TabList>
    <Tab value="overview">Overview</Tab>
    <Tab value="leases" badge={<Badge size="sm">3</Badge>}>Leases</Tab>
    <Tab value="docs">Documents</Tab>
  </TabList>
  <TabPanel value="overview">…</TabPanel>
  <TabPanel value="leases">…</TabPanel>
  <TabPanel value="docs">…</TabPanel>
</TabStrip>
```

---

## 3. Props — semantics and defaults

### 3.1 `<TabStrip>` (root)

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string` | _required_ | Controlled active tab value. Matches a `<Tab>`'s `value` prop. |
| `onValueChange` | `(value: string) => void` | _required_ | Fires when the user activates a different tab. Host updates `value`. **Note for Telerik users (closes G-TS3):** Telerik uses `ActiveTabId` / `ActiveTabIdChanged`. Harborline follows Radix naming (`value` / `onValueChange`) for consistency with SelectField and other controlled primitives. |
| `orientation` | `'horizontal' \| 'vertical'` | `'horizontal'` | Layout direction. `'vertical'` stacks tabs in a column (TabList becomes a left rail). |
| `variant` | `'underline' \| 'pill' \| 'card'` | `'underline'` | Visual variant. `'underline'` is an underline-style active indicator (default). `'pill'` is a pill-shaped active background. `'card'` is the bordered-tab "folder" treatment. PAO Styling owns the token surface. |
| `activationMode` | `'automatic' \| 'manual'` | `'automatic'` | Radix activation mode. `'automatic'` — arrow-key focus also activates the focused tab. `'manual'` — arrow-key focus moves focus but does not activate; user must press Enter/Space. |
| `children` | `ReactNode` | _required_ | TabList + TabPanels composition. |

### 3.2 `<TabList>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `children` | `ReactNode` | _required_ | One or more `<Tab>` instances. |
| HTML attributes | — | — | Spread onto the root `<div role="tablist">`. |

### 3.3 `<Tab>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string` | _required_ | Unique identifier for this tab; matches the active value on `<TabStrip>` and the `value` on a `<TabPanel>`. |
| `disabled` | `boolean` | `false` | When `true`, the tab is non-interactive and **skipped in arrow-key navigation** — this is Radix's default behavior and is deliberate; keyboard users jump directly over disabled tabs. This behavior will not change. **Closes G-TS4.** |
| `badge` | `ReactNode` | — | Optional trailing badge (typically a Badge instance — count, "New", etc.). |
| `children` | `ReactNode` | _required_ | The tab label (typically text; optionally an icon + text). |
| HTML attributes | — | — | Spread onto the `<button role="tab">`. |

### 3.4 `<TabPanel>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string` | _required_ | Identifier matching a `<Tab>`'s `value`. |
| `forceMount` | `boolean` | `false` | When `true`, the panel is always mounted (even when not active). Use for panels whose content has expensive mount cost or must preserve state across tab switches. Radix-supported. |
| `children` | `ReactNode` | _required_ | Panel content. |
| HTML attributes | — | — | Spread onto the `<div role="tabpanel">`. |

### 3.5 Orientation semantics

| Orientation | TabList layout | Arrow keys for navigation |
| --- | --- | --- |
| `'horizontal'` | Horizontal row | Left / Right |
| `'vertical'` | Vertical column | Up / Down |

Radix handles the orientation-aware key bindings automatically; the
contract just names the convention.

### 3.6 Variant semantics

| Variant | Visual treatment |
| --- | --- |
| `'underline'` | Tab triggers are plain text; active tab has an underline indicator. Default. |
| `'pill'` | Tab triggers are pill-shaped; active tab has a filled background. |
| `'card'` | Tab triggers are bordered boxes; active tab "merges" with the content area below (classic folder-tab look). |

PAO Styling owns the exact tokens.

### 3.7 Controlled vs uncontrolled

TabStrip is **controlled-only** in M2 — `value` and `onValueChange` are
required. Uncontrolled mode (with `defaultValue`) is deferred (council
open question 1) because most Harborline use cases bind the active tab to a
URL search-param or route segment, which is naturally controlled.

### 3.8 HTML attribute passthrough

Every sub-component spreads HTML attributes onto its rendered element.
Following the Button precedent for M2.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onValueChange` | `string` (the `value` of the activated tab) | The user activates a tab — click, Enter/Space on focused tab, or arrow-key in `'automatic'` activation mode. |

TabStrip does not expose `onFocus` / `onBlur` / per-tab callbacks in M2.

---

## 5. Slots

TabStrip uses named sub-components rather than slot props:

| Sub-component | Purpose | Cardinality |
| --- | --- | --- |
| `<TabList>` | Container for `<Tab>` instances. | Exactly 1 |
| `<Tab>` | A single tab trigger. | 1+ inside `<TabList>` |
| `<TabPanel>` | Content for a tab. | 1+ as direct children of `<TabStrip>` (siblings of `<TabList>`) |

Within a `<Tab>`, the `badge` slot prop accepts a trailing Badge instance.

Within a `<TabPanel>`, the `children` is the panel content (any ReactNode).

---

## 6. Component composition

- **AppBar / AppLayout under-bar.** A common pattern places a TabStrip
  immediately below AppBar in the page-header region, showing
  sub-navigation for the current route. The TabStrip's `'underline'`
  variant + horizontal orientation reads as natural sub-nav.
- **Card with tabs.** TabStrip inside a Card's `<CardHeader>` lets a
  card switch between views without forcing the host to render multiple
  cards. The TabList lives in CardHeader; TabPanels live in CardContent.
- **Vertical tabs in settings.** A vertical TabStrip with `'pill'` or `'card'`
  variant inside a settings page gives a left-rail navigation for the
  settings sub-sections. The TabPanels span the remaining right-side
  area.
- **Filter switcher.** TabStrip can drive a "All / Outstanding / Paid"
  filter row above a DataGrid. The TabPanel for each tab contains the
  DataGrid with the matching filter applied; alternatively, hosts
  use a single TabPanel and switch the filter prop separately based on
  `value`.
- **URL synchronisation.** Hosts typically sync `value` to a URL
  search-param: `?tab=leases`. TabStrip stays controlled; the URL is the
  source of truth.
- **Badge integration.** Tabs consume Badge directly via the `badge`
  prop. Badge.Semantic.md governs.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Uncontrolled mode** (`defaultValue` prop) — controlled-only in M2.
- **Closeable tabs (closes G-TS2)** — a "×" close button on each tab
  trigger to dismiss it. Telerik TabStrip has a `Closeable` prop on each
  tab and an `OnTabRemove` callback on the strip for this pattern. Harborline
  TabStrip has no equivalent in M2 — neither a `closeable` prop on `<Tab>`
  nor an `onTabClose` callback on `<TabStrip>`. Required for IDE-style
  tabbed editors and multi-document workflows; out of scope for app
  sub-navigation. Planned for a future wave alongside `onTabReorder`.
- **Tab reordering** (drag-and-drop). IDE-pattern; deferred.
- **Overflow handling** — scrollable horizontal tabs when they exceed
  available width, with arrow scroll buttons. M2 baseline lets tabs
  overflow naturally; hosts must keep tab count modest.
- **Lazy panel mounting with persisted state** — Radix's `forceMount`
  prop provides static-mount; full lazy-with-cache is a future
  enhancement.
- **Right-side actions in TabList** — a small action area next to the
  tabs (e.g. a "+" button to add a new tab). Hosts compose externally.
- **Sub-tabs / nested TabStrips** — multiple TabStrip instances inside
  a panel work natively; the contract notes this works but does not
  add a special API.

---

## 8. Open questions (for council)

1. **Uncontrolled mode.** Adding `defaultValue?: string` is trivial
   (Radix supports it directly). Worth adding for M2? Leaning defer —
   Harborline use cases are URL-bound; controlled-only keeps the surface
   small and the contract simpler.
2. **Default activation mode.** `'automatic'` (this spec, Radix default)
   or `'manual'`? `'automatic'` lets keyboard users navigate by arrow
   alone (a focus move IS an activation); `'manual'` requires Enter/Space
   to commit. Leaning `'automatic'` for parity with Radix default; PAO
   Accessibility may want `'manual'` for some patterns (council confirm).
3. **Default variant.** `'underline'` (this spec) is the lightest treatment;
   `'pill'` is more visually distinct; `'card'` is the folder
   classic. Leaning `'underline'` — least chrome, works in most contexts.
4. **Vertical-orientation default variant.** `'underline'` indicator doesn't
   work for vertical tabs (an underline on a vertical tab is visually
   awkward). Should vertical orientation default to `'pill'`?
   Leaning yes — vertical tabs naturally read as pills or card-enclosed.
5. **`<TabPanel>` sibling vs nested-in-TabList composition.** This spec
   places TabPanels as siblings of TabList (Radix-canonical). Some
   teams prefer Tab + TabPanel pairs colocated for readability:
   `<Tab value="x" panel={<…>}>`. Leaning Radix-canonical — composition
   stays explicit; sibling structure makes the DOM cleaner.
6. **TabPanel unmount-on-inactive behaviour.** Default Radix behaviour
   unmounts inactive panels (heavy content disposes on tab-switch).
   `forceMount` is an opt-in. Should we change the default to keep
   panels mounted (preserving state more like a tabbed editor)?
   Leaning Radix default — unmount; hosts opt-in to `forceMount` for
   state-heavy panels.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/navigation/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TS1 | Critical | Overflow handling deferred — material UX gap | [RESOLVED 2026-06-05] §1 critical callout added; §7 overflow roadmap M2.1; max 4 tabs on mobile in M2 |
| G-TS2 | High | Closeable tabs deferred | [ACCEPTED-RISK 2026-06-05] §7: IDE-style tabs; out of scope for M2 |
| G-TS3 | High | `value`/`onValueChange` API name diverges from Telerik | [RESOLVED 2026-06-05] §3.1 explicit note: Harborline follows Radix naming; Telerik uses ActiveTabId/ActiveTabIdChanged |
| G-TS4 | High | `<Tab disabled>` arrow-key skipping should be named | [RESOLVED 2026-06-05] §3.3: disabled tabs are skipped in arrow-key navigation (Radix default; deliberate) |
| G-TS5 | Medium | Sub-tabs recipe absent | [ACCEPTED-RISK 2026-06-05] §7 note: nested TabStrip works natively; no special API |
| G-TS6 | Medium | Drag-reorder deferred | [ACCEPTED-RISK 2026-06-05] §7: IDE-pattern; out of scope |
| G-TS7 | Medium | `<TabPanel forceMount>` with no matching value | [ACCEPTED-RISK 2026-06-05] §2.3 note: all panels hidden if value matches no Tab; prior active stays inactive |
