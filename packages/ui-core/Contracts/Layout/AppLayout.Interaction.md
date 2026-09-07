# AppLayout — Interaction Contract

- **Component:** AppLayout
- **ADR 0017 family:** Layout
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppLayout.Semantic.md) · [Styling](./AppLayout.Styling.md) · [Accessibility](./AppLayout.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/layout/AppLayout.tsx`
- **Catalog row:** A10 AppLayout / Shell (`app-priority: high`, `library-scope: v1`, source: Vaadin AppLayout)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

AppLayout's interaction surface is small but non-trivial. The component owns:

- The **side-nav open/close behaviour** in `'overlay'` mode (backdrop click,
  ESC, focus trap).
- The **`'auto'` mode breakpoint resolution** that swaps `'rail'` ↔
  `'overlay'` as the viewport crosses the breakpoint.
- The **scroll-container ownership** (`contentScroll` mode).

It does **not** own:

- Side-nav opening (host-driven, typically via an AppBar Button).
- Route transitions or navigation logic.
- Theme / dark-mode toggling.

---

## 2. Side-nav open/close

### 2.1 Opening

AppLayout never opens the side-nav itself. Opening is a host action:

```tsx
const [navOpen, setNavOpen] = useState(false)
<AppLayout
  header={<AppBar start={<Button onClick={() => setNavOpen(true)}>☰</Button>} />}
  sideNavOpen={navOpen}
  onSideNavOpenChange={setNavOpen}
  …
>
```

### 2.2 Closing — overlay mode

When `sideNavMode` resolves to `'overlay'`, AppLayout fires
`onSideNavOpenChange(false)` in response to:

- **Backdrop click** — clicking the overlay scrim behind the side-nav.
- **ESC press** — when focus is inside the side-nav or its scrim.
- **Explicit close from `sideNav` content** — the SideNav component may
  render its own close button which propagates through the
  `onSideNavOpenChange` callback. AppLayout does not bake in a close
  button; SideNav (or the host) owns that.

After firing the callback, AppLayout waits for the host to update
`sideNavOpen` and re-renders without the overlay.

### 2.3 Closing — rail mode

When `sideNavMode` resolves to `'rail'`, the side-nav cannot be "closed"
per se — it collapses to the icon-only rail width. Toggling
`sideNavOpen: false` triggers the collapse; `true` triggers the expand.
No backdrop, no ESC handling, no focus trap (rail-mode SideNav is part of
normal tab order).

### 2.4 Closing — hidden mode

`sideNavMode === 'hidden'` ignores `sideNavOpen` entirely. No
open/close behaviour.

---

## 3. `'auto'` mode breakpoint resolution

When `sideNavMode === 'auto'`:

- AppLayout subscribes to a viewport-width media query (canonical `md`,
  768px — token-owned by PAO Styling).
- **≥ breakpoint:** resolved mode is `'rail'`. `sideNavOpen` controls
  expanded vs collapsed rail.
- **< breakpoint:** resolved mode is `'overlay'`. `sideNavOpen` controls
  visible vs hidden overlay.

### 3.1 Mode transitions

When the viewport crosses the breakpoint:

- **Wide → narrow** (e.g. user resizes the window): mode flips from
  `'rail'` to `'overlay'`. **`sideNavOpen` does NOT auto-reset** — if
  the user had the rail expanded, the overlay opens (which may be
  surprising). Council open question 1 considers whether to auto-close.
- **Narrow → wide:** mode flips from `'overlay'` to `'rail'`. The rail
  reflects the current `sideNavOpen` value (expanded or collapsed).

This is a deliberate "preserve user intent" choice; if it tests poorly,
the alternative is "auto-close on breakpoint crossing" (council open
question 1).

---

## 4. Focus management

### 4.1 Overlay mode + open

When the side-nav is open in overlay mode, AppLayout traps focus inside
the side-nav region:

- On open, focus moves to the first focusable element inside `sideNav`
  content (PAO Accessibility owns the exact target — typically the close
  button, or the first nav item).
- Tab cycles focus within the side-nav region; Shift+Tab cycles
  backwards. Focus does not leak to the main content.
- On close, focus returns to the element that opened the side-nav
  (typically the AppBar hamburger Button). AppLayout records the opener
  via standard "previously-focused element" tracking.

PAO Accessibility owns the ARIA + announcements; this contract covers the
focus mechanics.

### 4.2 Rail mode

No focus trap. Side-nav participates in normal Tab order:
header → sideNav → main → (browser chrome).

### 4.3 Hidden mode

No side-nav, no focus mechanics specific to AppLayout.

---

## 5. Scroll container ownership

### 5.1 `contentScroll: 'main'` (default)

The grid root has `height: 100vh`. The main region has
`overflow-y: auto`. Header and sideNav remain visually pinned because
their grid cells do not scroll; only the main cell does.

Implications:

- The `window.scrollY` stays 0 across normal interaction.
- Hosts must use the main-region scroll container for any scroll-to-top
  / scroll-restoration behaviour (canonical pattern: AppLayout exposes a
  ref to the main region, or the host queries
  `document.querySelector('[data-applayout-main]')`).

### 5.2 `contentScroll: 'page'`

Document body owns the scroll. Header (if `headerFixed: true`) overlays;
otherwise it scrolls with the page.

- Standard `window.scrollTo(0, 0)` works for scroll-to-top.
- AppBar's `position: 'sticky'` continues to work because sticky elements
  are page-flow-aware.

---

## 6. Keyboard behaviour

| Key | Context | Behaviour |
| --- | --- | --- |
| Tab / Shift+Tab | Default | Standard focus traversal: header → sideNav (rail) → main. |
| Tab / Shift+Tab | Overlay side-nav open | Focus trapped within sideNav region. |
| ESC | Overlay side-nav open | Fires `onSideNavOpenChange(false)`. |
| ESC | Any other context | No AppLayout-specific behaviour. |

---

## 7. Resize behaviour

AppLayout listens for viewport resizes only when `sideNavMode === 'auto'`
(to flip the breakpoint). Other modes have no resize-driven behaviour.

The implementation uses a standard `matchMedia` listener (no polling, no
ResizeObserver on the layout itself).

---

## 8. Interaction-state precedence

When multiple state conditions hold (e.g. side-nav open + resize event +
ESC press), resolve in this order (highest precedence first):

1. **Mode change via resize** (auto mode crossing the breakpoint) —
   re-renders with the new resolved mode. Focus trap engages/disengages
   accordingly.
2. **Explicit close** (backdrop click, ESC) — fires
   `onSideNavOpenChange(false)`; host updates state; AppLayout re-renders
   with side-nav closed; focus returns to opener.
3. **Explicit open from host** — `sideNavOpen: true` arrives via prop;
   AppLayout renders side-nav open; focus moves into side-nav (overlay
   mode only).
4. **Normal interaction** — main-region scrolling, button activations,
   etc.

---

## 9. Council open questions (Interaction)

1. **Auto-close on `'auto'` breakpoint crossing.** When viewport shrinks
   below the breakpoint while side-nav is open, should AppLayout
   auto-fire `onSideNavOpenChange(false)`? This spec preserves the
   open state (overlay appears). Auto-close trades surprise (overlay
   appears) for closing what may have been a deliberate desktop choice
   (rail expanded). Leaning preserve — let host decide via the
   callback.
2. **Backdrop click suppression.** Some hosts want to require an
   explicit close action (no incidental backdrop dismissal). Should we
   add `closeOnBackdropClick?: boolean` (default `true`)? Leaning yes,
   add the prop with default `true` — cheap, follows Dialog precedent.
3. **ESC suppression.** Same question for ESC. Leaning add
   `closeOnEscape?: boolean` (default `true`) by symmetry with the
   backdrop opt-out.
4. **Focus-target ownership.** Where does focus go on overlay open?
   First focusable in side-nav? An explicit `initialFocusRef`?
   Leaning first focusable + PAO Accessibility ratification — escape
   hatch (`initialFocusRef`) deferred.
5. **Main-region scroll exposure.** Hosts need a ref to the main
   scroll container for scroll-restoration. Should AppLayout expose a
   `mainRef?: Ref<HTMLDivElement>` prop, or use a `data-applayout-main`
   attribute hosts query? Leaning attribute — fewer ref-forwarding
   gymnastics; documented well enough.
6. **Mode-transition animations.** Should AppLayout animate the
   rail-to-overlay transition (or vice versa)? Today: no — hard cut.
   Animation is divisive and pulls in framer-motion or similar.
   Leaning hard cut.
