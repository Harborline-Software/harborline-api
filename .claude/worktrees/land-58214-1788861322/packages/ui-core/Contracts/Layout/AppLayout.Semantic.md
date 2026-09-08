# AppLayout — Semantic Contract

- **Component:** AppLayout
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./AppLayout.Interaction.md) · [Styling](./AppLayout.Styling.md) · [Accessibility](./AppLayout.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/AppLayout.tsx`
- **Catalog row:** A10 AppLayout / Shell (`app-priority: high`, `library-scope: v1`, source: Vaadin AppLayout)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** Vaadin AppLayout convention — CSS grid shell with named slot regions
- **Related contracts:** [AppBar.Semantic.md](../Navigation/AppBar.Semantic.md) · [SideNav.Semantic.md](../Navigation/SideNav.Semantic.md)

---

## 1. Purpose

AppLayout is the canonical **application shell** of `@harborline-software/ui-react`. It
is the outermost component of a Harborline app's React tree (below the router
provider) and owns the three-region page grid: a top bar (AppBar), an
optional left rail (SideNav), and the main content. It is the structural
backbone every authenticated route renders into.

This is a **forward-spec**: AppLayout has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the Vaadin AppLayout convention, adapted to Harborline's slot
vocabulary. The shipping target lives at
`packages/ui-react/src/components/layout/AppLayout.tsx` and will follow this
spec.

AppLayout's responsibilities:

- **Page-grid layout** — three named regions (`header`, `sideNav`, `main`)
  laid out as a CSS grid.
- **Side-nav open/close state** — for mobile (overlay) and collapsible
  (rail) treatments. Controlled by the host.
- **Top-offset compensation** — when AppBar is `position: fixed`, AppLayout
  pads the `main` region to avoid overlap.
- **Viewport height** — main region fills the viewport with internal scroll;
  AppBar and SideNav remain visible while the main content scrolls.

AppLayout does **not** own routing, theme, or auth — those are higher-level
providers wrapped around AppLayout by the host application.

---

## 2. Data model

AppLayout has no internal data model; side-nav state is host-controlled.

```typescript
type SideNavMode = 'rail' | 'overlay' | 'hidden'

interface AppLayoutProps extends React.HTMLAttributes<HTMLDivElement> {
  // Slot content
  header?: React.ReactNode
  sideNav?: React.ReactNode
  children: React.ReactNode

  // Side-nav state
  sideNavOpen?: boolean
  onSideNavOpenChange?: (open: boolean) => void
  sideNavMode?: SideNavMode | 'auto'

  // Layout tuning
  headerFixed?: boolean
  contentScroll?: 'main' | 'page'
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `header` | `ReactNode` | — | Top-bar content. Typically an AppBar instance. When omitted, the layout has no top region. |
| `sideNav` | `ReactNode` | — | Side-rail content. Typically a SideNav instance. When omitted, no rail; the layout is single-column. |
| `children` | `ReactNode` | _required_ | Main page content. Renders into the central region of the grid. |
| `sideNavOpen` | `boolean` | `true` (`'rail'` + `'overlay'` modes); ignored otherwise | Controlled open/close state for the side rail. In `'rail'` mode controls expanded vs collapsed; in `'overlay'` mode controls visible vs hidden. |
| `onSideNavOpenChange` | `(open: boolean) => void` | — | Fires when AppLayout (or SideNav itself) requests an open-state change (overlay backdrop click, ESC press in overlay mode, etc.). |
| `sideNavMode` | `'rail' \| 'overlay' \| 'hidden' \| 'auto'` | `'auto'` | Controls how the side rail is presented. See §3.1. `'auto'` picks `'rail'` on wide viewports and `'overlay'` on narrow viewports per a fixed breakpoint (token-owned by PAO Styling, canonical `md`/768px). |
| `headerFixed` | `boolean` | `false` | When `true`, AppLayout positions the header region absolutely so it overlays the grid; main content gets a top-offset padding equal to the header height. Use only when `header` content (AppBar) is itself `position: fixed`. |
| `contentScroll` | `'main' \| 'page'` | `'main'` | `'main'` — only the main region scrolls (header + sideNav stay visible). `'page'` — the entire document scrolls (header + sideNav scroll with the page; only useful if header has its own sticky positioning). |
| HTML attributes | — | — | Spread onto the root `<div>`. |

### 3.1 `sideNavMode` semantics

| Mode | Visual | Open state meaning |
| --- | --- | --- |
| `'rail'` | Always-visible left rail; takes its own grid column. | `sideNavOpen: true` → expanded width (canonical 240px). `false` → collapsed width (icon-only rail, canonical 64px). |
| `'overlay'` | Rail is absent from grid by default; slides in over the main region when open. | `sideNavOpen: true` → rail visible as overlay + backdrop. `false` → rail hidden. |
| `'hidden'` | Rail is not rendered at all. | `sideNavOpen` ignored. |
| `'auto'` | Resolves to `'rail'` at viewport ≥ `md` (768px), `'overlay'` below. | Behaviour follows the resolved mode. |

PAO Styling owns the breakpoint and the rail widths; this contract names the
behaviour.

### 3.2 Grid layout

The canonical layout is a CSS grid with three regions:

```
+--------------------------------+
|           header               |
+----------+---------------------+
| sideNav  |        main         |
|          |                     |
|          |                     |
+----------+---------------------+
```

- `header` spans the full width across the top.
- `sideNav` is the left column below the header (in `'rail'` mode).
- `main` (i.e. `children`) is the central region.

When `sideNav` is omitted (or mode is `'hidden'`), the grid collapses to a
two-row single-column layout (header + main).

When `header` is omitted, the grid collapses to a single-row layout
(sideNav + main, or main only).

### 3.3 Viewport sizing

The root grid is sized to fill the viewport (`h-screen` equivalent). When
`contentScroll === 'main'` (default), the `main` region carries the scroll
container (`overflow-y: auto`); header and sideNav stay pinned in their grid
cells.

When `contentScroll === 'page'`, the grid uses `min-h-screen` instead, the
main region grows to fit its content, and the document body owns the
scroll.

### 3.4 HTML attribute passthrough

AppLayout spreads HTML attributes onto the root `<div>`. The root element is
a `<div>` rather than `<main>`/`<section>` because the `main` region inside
is the page's `<main>` landmark (PAO Accessibility owns the landmark
wiring).

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onSideNavOpenChange` | `boolean` (next state) | The user closes the side nav via:<br>• overlay-backdrop click (overlay mode)<br>• ESC press (overlay mode)<br>• explicit close action inside `sideNav` content<br>Hosts then update `sideNavOpen` to the new value. |

AppLayout does **not** open the side-nav on its own. Opening is a host
action: the host typically hooks a "hamburger" Button (in AppBar's `start`
slot) to set `sideNavOpen: true`.

---

## 5. Slots

| Slot prop | Region | Typical content |
| --- | --- | --- |
| `header` | Top | AppBar instance. |
| `sideNav` | Left | SideNav instance. |
| `children` | Main | Route content (typically a router outlet). |

Named slots are prop-driven (no sub-components). The host composes:

```tsx
<AppLayout
  header={<AppBar start={<Brand />} end={<UserMenu />} />}
  sideNav={<SideNav items={navItems} />}
  sideNavOpen={open}
  onSideNavOpenChange={setOpen}
>
  <Outlet />
</AppLayout>
```

---

## 6. Component composition

- **Top of every authenticated route.** AppLayout wraps the router's
  `<Outlet>` (React Router) or equivalent so every route renders into the
  `main` region.
- **AppBar + SideNav pairing.** The canonical shell composition: AppBar in
  `header`, SideNav in `sideNav`, route content in `children`.
- **Unauthenticated routes.** Sign-in / sign-up pages typically render
  *outside* AppLayout (the host's route tree branches before AppLayout
  appears). AppLayout has no opinion on auth.
- **Settings / wizard layouts** that want a wider main region with no
  side-nav typically pass `sideNavMode='hidden'` to reuse AppLayout's
  header + main grid without the rail.
- **Mobile drawer pattern.** On narrow viewports, the SideNav becomes an
  overlay (`'auto'` mode). The host wires the AppBar hamburger Button to
  toggle `sideNavOpen`.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Right-rail / inspector panel** — a second side region on the right
  for properties / details inspectors. May arrive as `inspector` slot or
  as a separate `<InspectorPanel>` component.
- **Footer slot** — a persistent bottom bar. Apps typically render footer
  content inside `children`. May add `footer` slot later.
- **Multiple side-nav levels** — secondary nav alongside primary. Owned
  by SideNav (catalog A11), not AppLayout.
- **Theme switcher integration** — AppLayout has no theme prop;
  light/dark is owned by an outer theme provider.
- **Sub-route transition animations** — fade/slide between route swaps.
  Hosts wrap their own animation primitive around `<Outlet>`.
- **Persisted `sideNavOpen` to localStorage** — hosts wire persistence
  themselves; AppLayout stays controlled.

---

## 8. Open questions (for council)

1. **Default `sideNavOpen`.** This spec defaults to `true` (expanded
   rail). Should the default be `false` (collapsed) to favour content
   space, or `true` (expanded) to favour discoverability? Leaning
   `true` — most users want nav visible by default; collapsed is the
   user's explicit choice.
2. **`sideNavMode='auto'` breakpoint.** Hard-coded `md` (768px) per the
   canonical Tailwind breakpoint. Should this be a token-overridable
   prop, or stay fixed? Leaning fixed — token-overridable adds
   complexity for a corner case.
3. **`contentScroll` default.** `'main'` (this spec) — only main region
   scrolls — vs `'page'` (full document scroll). Leaning `'main'` — app
   shells typically want persistent header/rail; `'page'` is the
   legacy/marketing case.
4. **Sub-component composition.** Should AppLayout expose
   `<AppLayout.Header>` / `<AppLayout.SideNav>` sub-components instead
   of (or in addition to) the slot props? Leaning slot props — the
   shell has at most three regions and named slots are clearer.
5. **`<main>` element on the central region.** Should AppLayout emit
   `<main>` for the central region, or leave the landmark to the host?
   Leaning emit `<main>` — it's a structural decision AppLayout
   genuinely owns; PAO Accessibility confirms.
6. **AppBar `position: 'fixed'` + AppLayout `headerFixed: false`
   mismatch.** What happens? Leaning: document as host error; the host
   must keep them aligned. Optionally warn in dev mode.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/layout/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
