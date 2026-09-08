# AppLayout — Accessibility Contract

- **Component:** AppLayout
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppLayout.Semantic.md) · [Interaction](./AppLayout.Interaction.md) · [Styling](./AppLayout.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Vaadin AppLayout_
- **Catalog row:** A10 AppLayout / Shell (`app-priority: high`, `library-scope: v1`, source: Vaadin AppLayout)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

AppLayout is the application shell. Its accessibility responsibilities
centre on landmark composition (it hosts banner + navigation + main
landmarks), responsive focus management (mobile drawer focus trap +
return-focus), and the body-scroll lock that the drawer requires.

This contract pins:

1. The three landmark regions composed by AppLayout (banner from AppBar,
   navigation from SideNav, main from the content slot).
2. The `<main id="main">` requirement for skip-nav target binding.
3. The mobile-drawer focus trap (delegated to SideNav's drawer mode).
4. The body-scroll lock semantics.
5. Sticky-AppBar interaction with focus visibility (`scroll-margin-top`).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Landmark composition

AppLayout's children carry the landmarks; AppLayout itself emits no role.

| Region | Landmark | Owned by |
|---|---|---|
| AppBar | `role="banner"` (via `<header>` element) | AppBar contract |
| SideNav | `role="navigation"` (via `<nav>` element) | SideNav contract |
| Main content | `role="main"` (via `<main>` element) | AppLayout (via the `<main id="main">` it renders) |

The `<main>` element MUST:

- Carry `id="main"` so the AppBar skip-nav link can target it.
- Be the SOLE `role="main"` on the page (landmark uniqueness).
- Wrap ALL page-specific content (the page's `<h1>` lives inside `<main>`).

**Landmark uniqueness rules:**

- Exactly one `banner` (from AppBar).
- Exactly one `navigation` from SideNav (additional `<nav>` regions
  elsewhere on the page MUST carry `aria-label` to distinguish them, e.g.,
  breadcrumb nav, in-page tabs).
- Exactly one `main`.

**WCAG citations:**
- SC 1.3.1 Info and Relationships — landmarks expose structure.
- SC 2.4.1 Bypass Blocks — landmarks ARE the bypass mechanism.

---

## 3. Skip-nav target binding

The AppBar emits a skip-nav link `<a href="#main">` (per AppBar
Accessibility §3). AppLayout MUST render the matching target:

```html
<main id="main" tabindex="-1">
  ...
</main>
```

**Why `tabindex="-1"` on `<main>`.** When the skip-nav link is activated,
the browser scrolls `#main` into view but does NOT move focus to it
automatically (since `<main>` is not natively focusable). `tabindex="-1"`
makes `<main>` programmatically focusable so the activation moves focus
into the main region, where the user's next Tab keypress lands on the
first interactive element inside main rather than re-traversing AppBar.

**WCAG citation:** WCAG 2.2 SC 2.4.1 Bypass Blocks.

---

## 4. Mobile drawer — focus management

When the AppBar's hamburger toggle opens the SideNav drawer on mobile, the
focus management is a multi-component dance:

| Event | Required focus behaviour | Owned by |
|---|---|---|
| Hamburger toggle activated → drawer opens | Focus moves to the first focusable element inside the drawer (typically the first nav item OR a "Close" button) | SideNav drawer mode |
| Drawer is open → Tab traversal | Focus is TRAPPED inside the drawer (Tab from the last focusable wraps to the first; Shift+Tab from first wraps to last) | SideNav drawer mode |
| Escape key pressed | Drawer closes | SideNav drawer mode |
| Drawer closes via any path (Escape, scrim click, toggle re-clicked) | Focus returns to the hamburger toggle in AppBar | SideNav drawer mode + AppBar |

The contract OWNS the requirement (focus trap, return focus) but the
implementation lives in SideNav's drawer mode + AppBar's toggle handler.
AppLayout is the layout that wires them together.

**WCAG citations:**
- SC 2.1.2 No Keyboard Trap — the trap is escapable (Escape closes).
- SC 2.4.3 Focus Order — focus order inside the drawer is sensible (top
  to bottom).
- SC 3.2.1 On Focus — opening the drawer is intentional user action.

---

## 5. Body-scroll lock during drawer-open

When the mobile drawer is open, AppLayout MUST lock body scroll. The
typical implementation:

```js
useEffect(() => {
  if (drawerOpen) {
    document.body.style.overflow = 'hidden';
    return () => { document.body.style.overflow = ''; };
  }
}, [drawerOpen]);
```

**Why scroll-lock matters.** Without it:

- Touch swipes inside the drawer can bleed through to scroll the page
  below (mobile chromeless overlays).
- Page-up / page-down / arrow keys inside the drawer can scroll the
  background page, leading to disorienting page motion the user did not
  request.

**iOS Safari caveat.** `overflow: hidden` alone does NOT prevent body
scroll on iOS Safari. The implementation MUST also handle iOS Safari's
quirks (`position: fixed; top: -scrollY` + restoring on close). The
forward-spec implementation will need a battle-tested library
(e.g., `body-scroll-lock`) or equivalent.

**WCAG citation:** WCAG 2.2 SC 2.5.7 Dragging Movements (mobile touch
scroll inadvertently triggers via drag → SC 2.5.7 violation if the
drawer cannot be operated with single pointer); SC 1.3.4 Orientation
(unrelated but tangential — both concern mobile UX).

---

## 6. Sticky-AppBar + focus visibility

When AppBar uses `position: sticky; top: 0`, focused elements lower in
`<main>` MAY scroll partially under the AppBar. WCAG 2.2 SC 2.4.11 Focus
Not Obscured (Minimum) REQUIRES that the focused element NOT be entirely
hidden by author-positioned overlays.

**Required CSS at the AppLayout level:**

```css
main {
  scroll-margin-top: var(--sf-appbar-height);
}

/* Or more specifically, on focusable elements within main: */
main :is(a, button, input, select, textarea, [tabindex]) {
  scroll-margin-top: var(--sf-appbar-height);
}
```

The `scroll-margin-top` rule ensures the browser's "scroll into view"
behaviour (triggered by `Element.focus()` or by clicking an in-page
anchor) leaves the AppBar's height clear above the focused element.

**WCAG citation:** WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum).

---

## 7. Keyboard

AppLayout itself binds no keyboard handlers. Each region's keyboard
contract is owned by that region's component contract.

The composed keyboard model:

| Initial state | First Tab | Notes |
|---|---|---|
| Page-loaded, no prior focus | Skip-nav link in AppBar | The skip-nav is the FIRST focusable element on the page |
| Skip-nav activated (Enter) | First focusable element inside `<main>` | Focus moves to `<main>`'s `tabindex="-1"` first, then Tab moves forward |
| Drawer-open (mobile) | First focusable inside the drawer | Tab traversal is trapped inside the drawer |
| Drawer-closed (mobile) | AppBar's hamburger toggle (return-focus target) | After drawer dismisses |

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; SC 2.4.3 Focus Order.

---

## 8. Color contrast

AppLayout has no visual chrome of its own; contrast is owned by AppBar,
SideNav, and the page-background token (`--sf-applayout-bg`).

| Surface | Minimum ratio | Owned by |
|---|---|---|
| Page background vs. content surfaces | 3:1 | AppLayout (`--sf-applayout-bg` vs. `--sf-color-surface`) — ensures cards and AppBar visually separate from the page background |
| Drawer scrim background opacity | not a contrast issue per se | Verify scrim color is sufficiently dark that body content beneath is dimmed; typical `rgba(0,0,0,0.5)` |

**WCAG citation:** WCAG 2.2 SC 1.4.11 Non-text Contrast.

---

## 9. Reduced motion

The mobile drawer slide animation + scrim fade animation MUST honor
`prefers-reduced-motion: reduce` (see Styling §6).

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 10. Touch target

AppLayout has no interactive surface of its own. Each child component
owns its touch-target obligations.

---

## 11. Do / Don't

### Do

- Render exactly one `<main id="main" tabindex="-1">` so the skip-nav
  target works correctly.
- Apply `scroll-margin-top: var(--sf-appbar-height)` so focused elements
  clear sticky AppBar.
- Lock body scroll while the mobile drawer is open; restore on close.
- Use the documented z-index strata (chrome 30 / drawer 40 / overlay 50+).
- Delegate the mobile-drawer focus trap to SideNav's drawer mode (single
  source of truth).

### Don't

- Don't emit `role="main"` on `<main>` — it's redundant (implicit).
- Don't omit `tabindex="-1"` on `<main>` — skip-nav focus motion breaks.
- Don't render multiple `<main>` elements per page.
- Don't trap focus at the AppLayout level when the drawer is closed
  (only the drawer-open state traps).
- Don't skip the body-scroll lock — touch users will scroll the page
  beneath the drawer.
- Don't apply iOS-Safari-naive `overflow: hidden` only — use a tested
  scroll-lock approach.

---

## 12. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** Vaadin AppLayout pattern with
  React state hooks for drawer toggle.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-app-layout>` is
  the direct foundation.

---

## 13. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | `<main>` is emitted with `id="main"` AND `tabindex="-1"` | Snapshot test |
| F2 | Skip-nav from AppBar successfully moves focus into `<main>` | E2E test: tab to skip-nav, press Enter, verify focus is inside main |
| F3 | Body-scroll-lock works on iOS Safari (not just Chrome) | Manual mobile test on iOS Safari + Android Chrome |
| F4 | Drawer focus trap escapes correctly on Escape | E2E test: open drawer, Tab three times, press Escape, verify focus returns to hamburger toggle |
| F5 | `scroll-margin-top` keeps focused elements clear of sticky AppBar | E2E test: tab through a long page; verify no focused element is fully obscured |
| F6 | Single `role="banner"` / `navigation` / `main` per page (axe landmarks check) | Run axe scan; verify landmark uniqueness |
| F7 | `prefers-reduced-motion` suppresses drawer slide + scrim fade | Manual test with OS reduce-motion on; verify no animation |
| F8 | Right-rail / RTL composition is documented but NOT in M2 scope (track for M3) | Confirm in council |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog A10 AppLayout / Shell (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- Vaadin AppLayout — primary reference foundation
- [AppLayout.Semantic.md](./AppLayout.Semantic.md) — prop contract
- [AppLayout.Interaction.md](./AppLayout.Interaction.md) — behavioural contract
- [AppLayout.Styling.md](./AppLayout.Styling.md) — token surface + visual states
- [AppBar.Accessibility.md](../Navigation/AppBar.Accessibility.md) — banner + skip-nav
- [SideNav.Accessibility.md](../Navigation/SideNav.Accessibility.md) — navigation + drawer focus trap
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `main`, `banner`, `navigation`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.1 Bypass Blocks
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum)
- WCAG 2.2 SC 2.5.7 Dragging Movements
- WCAG 2.2 SC 3.2.1 On Focus
