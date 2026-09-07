# AppBar — Semantic Contract

- **Component:** AppBar
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./AppBar.Interaction.md) · [Styling](./AppBar.Styling.md) · [Accessibility](./AppBar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/AppBar.tsx`
- **Catalog row:** #4 AppBar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** native `<header>` with slot-based composition (no Radix primitive)
- **Related contract:** [AppLayout.Semantic.md](../Layout/AppLayout.Semantic.md) — AppBar is the
  top-bar slot of the canonical shell.

---

## 1. Purpose

AppBar is the canonical **top-of-app horizontal bar** of `@harborline-software/ui-react`.
It anchors the application shell: it shows the product identity (brand /
logo), surface-level page context (title, breadcrumbs), and the user's global
actions (search, notifications, profile menu). It is the persistent visual
anchor that tells the user "you are still inside the app" as routes change
underneath it.

This is a **forward-spec**: AppBar has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the Vaadin AppLayout + KendoReact AppBar conventions, adapted to
Harborline's three-slot vocabulary (start / center / end).

AppBar is a **slot container**, not a styled-text widget. The host owns the
content of each slot (brand mark, page title, action buttons); AppBar owns
the layout, height, sticky positioning, and accessibility role.

---

## 2. Data model

AppBar has no internal data model.

```typescript
type AppBarPosition = 'static' | 'sticky' | 'fixed'
type AppBarVariant = 'flat' | 'bordered' | 'elevated'
type AppBarSize = 'compact' | 'default' | 'large'

interface AppBarProps extends React.HTMLAttributes<HTMLElement> {
  position?: AppBarPosition
  variant?: AppBarVariant
  size?: AppBarSize
  start?: React.ReactNode
  center?: React.ReactNode
  end?: React.ReactNode
  children?: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | `'static' \| 'sticky' \| 'fixed'` | `'sticky'` | CSS positioning. `'sticky'` is the default (top-aligned, scrolls into place). `'fixed'` removes the bar from document flow. `'static'` is in-flow with no special positioning. |
| `variant` | `'flat' \| 'bordered' \| 'elevated'` | `'bordered'` | Visual treatment. `'flat'` is no border + no shadow. `'bordered'` has a bottom border. `'elevated'` has a small drop shadow. PAO Styling owns the tokens. |
| `size` | `'compact' \| 'default' \| 'large'` | `'default'` | Bar height. `'compact'` is a reduced-height variant (dense toolbars, embedded views). `'default'` is the standard shell height (≈56px). `'large'` is an expanded variant for prominent headers. PAO Styling owns the exact token values. |
| `start` | `ReactNode` | — | Left-aligned slot. Typical content: brand mark, hamburger menu toggle, back button. |
| `center` | `ReactNode` | — | Centre slot. Typical content: page title, breadcrumb trail, global search. **Optional** — many apps leave this empty. |
| `end` | `ReactNode` | — | Right-aligned slot. Typical content: notifications icon, help, profile menu, sign-in button. |
| `children` | `ReactNode` | — | Alternative to the three named slots: when supplied, hosts own the full internal layout. Mutually exclusive with `start` / `center` / `end` (council open question 1). |
| HTML attributes | — | — | Spread onto the root `<header>`. |

### 3.1 Position semantics

| Position | CSS | When the app should use it |
| --- | --- | --- |
| `static` | `position: static` | Page-flow positioning. Bar scrolls away when the user scrolls down. Rare for shell apps; useful for print views or embedded contexts. |
| `sticky` | `position: sticky; top: 0` (default) | Bar stays at the top of the viewport as the user scrolls. Default for shell apps because it preserves the user's anchoring without consuming layout slots above the fold. |
| `fixed` | `position: fixed; top: 0; left: 0; right: 0` | Bar is removed from document flow and always renders pinned to the top. Use when the shell wants the bar above all sibling content regardless of parent overflow. AppLayout handles top-offset compensation. |

### 3.2 Slot semantics

The three named slots (`start`, `center`, `end`) drive a three-region flex
layout: `[start]   [center]   [end]`. PAO Styling owns the exact gap /
padding / alignment tokens, but the structural intent is:

- `start` — left-aligned, hugs the start edge with standard padding.
- `center` — centre-aligned, takes the remaining horizontal space; truncates
  with `text-overflow: ellipsis` when content is too wide for the available
  region.
- `end` — right-aligned, hugs the end edge with standard padding.

When a slot is empty (or omitted), it occupies zero width and the adjacent
slots redistribute the space.

### 3.3 Height + density

The M2 baseline AppBar has a **fixed height** (token-owned by PAO Styling;
canonical `h-14` ≈ 56px for desktop). A density / size variant is deferred
(§7).

### 3.4 HTML attribute passthrough

AppBar spreads HTML attributes onto the root `<header>` element. The root
element is a `<header>` (not `<div>` or `<nav>`) for landmark semantics —
PAO Accessibility owns the landmark + ARIA-role decisions.

---

## 4. Events — semantics

AppBar has **no events**. Slot content (typically Button instances or a
profile menu) owns its own activation.

---

## 5. Slots

| Slot | Position | Typical content |
| --- | --- | --- |
| `start` | Left | Brand mark, hamburger menu toggle, back button. |
| `center` | Centre | Page title, breadcrumb trail, global search. |
| `end` | Right | Notifications, help, profile menu, sign-in. |
| `children` | Full bar | Host-owned full layout (mutually exclusive with named slots). |

There are no sub-components in M2 (compare Card, which has named
sub-components). The three-slot model is **prop-driven** to keep AppBar's
surface small.

---

## 6. Component composition

- **AppLayout integration (canonical).** AppBar is the canonical content
  for AppLayout's `header` slot. AppLayout owns the page-grid; AppBar
  owns the top-bar content.
- **Hamburger menu (mobile).** Hosts compose a Button with a menu-icon
  child into the `start` slot, with `onClick` toggling AppLayout's
  side-nav-open state. AppBar does not own the menu-state itself.
- **Page title + breadcrumb.** Hosts place a `<Breadcrumb>` (catalog
  #14, future wave) or plain text into the `center` slot.
- **Profile menu.** Hosts compose Button (avatar + name) into the `end`
  slot; activation typically opens a DropdownMenu (catalog #34, future
  wave).
- **Global search.** A SearchBox-like control (catalog future) goes
  into `center`, taking up most of the horizontal space.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Multi-row AppBar** (a second row for sub-navigation tabs under the
  main bar). The TabStrip placed below AppBar is the M2 pattern.
- **Auto-hide on scroll** (down → hide, up → show). Deferred.
- **Transparent / scroll-driven variant** (bar starts transparent over a
  hero image then becomes opaque on scroll). Marketing-page pattern;
  out of scope for ERP shell.
- **`color` / `theme` variant prop** — variants are structural-only;
  PAO Styling owns hue.
- **Built-in `onMenuClick` / `onLogoClick`** — hosts wire activations
  on the slot content directly.

---

## 8. Open questions (for council)

1. **`children` vs named slots.** This spec offers both. Should they be
   mutually exclusive (this spec), or should `children` always win and
   override the named slots? Leaning **mutually exclusive** — the named
   slots are the canonical API; `children` is the escape hatch.
2. **Default `position`.** `'sticky'` (this spec) or `'static'`? Leaning
   `'sticky'` — every shell app uses sticky; static is the rare case.
3. **Default `variant`.** `'bordered'` (this spec) or `'elevated'` or
   `'flat'`? Leaning `'bordered'` — a subtle bottom border is the
   least-distracting default that still creates visual separation.
4. **`<header>` vs `<nav>` element choice.** PAO Accessibility will
   decide the landmark role. AppBar contains nav-like content but
   typically isn't itself a nav landmark (the SideNav is). Leaning
   `<header>`; flag for PAO Accessibility ratification.
5. **`center` slot ellipsis behaviour.** Should AppBar enforce
   single-line truncation on `center` content, or let the host control
   it? Leaning enforce single-line + ellipsis — multi-line content in
   the bar breaks the fixed-height contract.
6. **Mobile breakpoint behaviour.** On narrow viewports, should AppBar
   hide the `center` slot to give priority to `start` + `end`? Or let
   the host decide via media queries on slot content? Leaning let the
   host decide — different apps prioritise differently.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/navigation/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
