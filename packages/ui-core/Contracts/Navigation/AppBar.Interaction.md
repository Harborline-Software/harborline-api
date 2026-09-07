# AppBar — Interaction Contract

- **Component:** AppBar
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppBar.Semantic.md) · [Styling](./AppBar.Styling.md) · [Accessibility](./AppBar.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/navigation/AppBar.tsx`
- **Catalog row:** #4 AppBar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

AppBar is a **slot container** with no internal interaction surface. This
contract documents:

- The no-interaction baseline (§2).
- The `position: sticky/fixed` scroll-anchored behaviour (§3).
- The keyboard and focus behaviour of slot content (§4).
- The mobile-breakpoint composition guidance (§5).

---

## 2. No interaction surface (root)

AppBar's root `<header>` is **not focusable** and emits **no events**. All
activation surfaces (buttons, menus, search inputs) are owned by the slot
content the host supplies. AppBar adds no:

- Click handlers on the bar itself.
- Internal state machines.
- Keyboard handlers.

Slot content interactions follow their own contracts (Button.Interaction.md
for buttons in `end`; future DropdownMenu / Breadcrumb / SearchBox for
other slot content).

---

## 3. Scroll behaviour

### 3.1 `position: 'static'`

AppBar is in document flow. It scrolls away with the page content. No
special behaviour to define.

### 3.2 `position: 'sticky'` (default)

Native CSS sticky positioning. The bar stays anchored to the top of its
nearest positioning ancestor (typically AppLayout's main grid) as the user
scrolls. No JavaScript-driven behaviour — the browser handles it.

- **Scroll-direction-aware hiding** (down → hide, up → show) is **not**
  part of M2 (Semantic §7 deferred).
- The bar's z-index is managed by PAO Styling so sticky elements inside
  the page content (e.g. table headers with `sticky top-0`) do not
  overlap the AppBar incorrectly.

### 3.3 `position: 'fixed'`

The bar is removed from document flow and pinned to the viewport top. The
parent shell (AppLayout) is responsible for offsetting its main content
below the bar (e.g. by adding `padding-top: var(--sf-appbar-height)`).
AppBar does not auto-pad sibling content.

---

## 4. Keyboard + focus behaviour

- **AppBar itself does not contribute a tab-stop.**
- **Slot content contributes its own tab-stops** in DOM order:
  - `start` slot content first (left-to-right within the slot).
  - `center` slot content next.
  - `end` slot content last.
- **Focus trap:** AppBar does **not** trap focus. Tab from `end` slot
  moves into the page content below the bar; Shift+Tab from `start`
  moves to the previous focusable element (often the document body or
  a skip-link).
- **Skip-link compatibility.** PAO Accessibility may add a "Skip to
  main content" link that visually appears on first Tab inside the
  document. The implementation contract is owned by PAO Accessibility;
  AppBar's structural contract supports it by not interposing focus.

---

## 5. Mobile-breakpoint behaviour

The contract takes **no position** on automatic mobile-breakpoint changes
(e.g. collapsing `center` content at narrow widths). Slot content is
host-owned, including its responsive behaviour.

Hosts who want a mobile-optimised pattern typically:

- Put a hamburger Button in `start` (visible only at narrow widths).
- Hide the `center` slot below a breakpoint (via Tailwind `hidden md:flex`
  on the slot content).
- Compact `end` slot actions (icon-only buttons below a breakpoint).

AppBar does not auto-collapse anything. This is a deliberate composition
choice — different apps have different mobile priorities.

---

## 6. Slot content swap behaviour

A common host pattern updates AppBar slot content per route (e.g. the
page title in `center` changes when the user navigates from "Properties"
to "Vendors"). AppBar handles slot-content changes as pure re-renders —
no transitions, no animations, no focus management.

If the host wants animated transitions (e.g. fade between page titles),
they wrap their slot content in their own animation primitive. AppBar
does not own this.

---

## 7. Interaction-state precedence

AppBar has no internal states to order. This section exists for template
parity; there are no precedence rules to specify.

---

## 8. Council open questions (Interaction)

1. **Scroll-direction auto-hide.** Should we add a `hideOnScrollDown?:
   boolean` opt-in in a follow-up wave, or defer indefinitely? Leaning
   defer — the pattern is divisive (some users love it, others find it
   disorienting) and the alternatives (always-sticky / always-static)
   cover the common cases.
2. **Z-index ownership.** AppBar must stack above page content (including
   any in-page `sticky` elements like table headers). The z-index token
   belongs to PAO Styling, but the contract should note the requirement
   so PAO Styling has a clear acceptance criterion. Confirm the wording.
3. **Skip-link integration.** Should AppBar bake in a "Skip to main
   content" focusable-link as a hidden first child, or leave it entirely
   to PAO Accessibility / AppLayout? Leaning leave to PAO — AppLayout
   owns the page grid and is the better home for the skip target.
4. **Focus return on route change.** When the host swaps slot content
   (e.g. page title changes), where does focus go? Today: nowhere —
   focus stays wherever the user put it (typically a focused link they
   just activated). This is correct for most cases but may be wrong for
   AT users. PAO Accessibility decides.
