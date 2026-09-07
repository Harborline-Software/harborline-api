# TabStrip — Accessibility Contract

- **Component:** TabStrip
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TabStrip.Semantic.md) · [Interaction](./TabStrip.Interaction.md) · [Styling](./TabStrip.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Radix Tabs_
- **Catalog row:** #131 TabStrip (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Tabs`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

TabStrip implements the WAI-ARIA 1.2 `tab` / `tablist` / `tabpanel`
authoring pattern. The pattern is one of the more demanding ARIA patterns:
it requires careful keyboard handling (arrow keys, NOT Tab, for in-list
navigation), explicit ARIA wiring between tabs and their panels, and a
choice between automatic and manual activation modes.

This contract pins:

1. The `tablist` / `tab` / `tabpanel` role triad.
2. The `aria-selected`, `aria-controls`, `aria-labelledby` wiring.
3. Keyboard model (arrow keys for list navigation, Tab to enter/exit,
   Space/Enter to activate in manual mode).
4. Automatic vs. manual activation choice.
5. Orientation-aware arrow keys.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Role triad

| Element | Role | Notes |
|---|---|---|
| Outer container holding tabs | `tablist` | One per TabStrip |
| Each tab button | `tab` | A `<button>` element with `role="tab"` |
| Each tab panel | `tabpanel` | A `<div>` element with `role="tabpanel"`; visible OR hidden based on selection |

### Required ARIA attributes

| Attribute | Where | Value |
|---|---|---|
| `aria-label` OR `aria-labelledby` | On the `tablist` | REQUIRED — gives the list an accessible name (e.g., "Property details") |
| `aria-orientation` | On the `tablist` | `"horizontal"` (default) or `"vertical"`; controls keyboard semantics |
| `id` | On each `tab` and each `tabpanel` | REQUIRED for `aria-controls` / `aria-labelledby` wiring |
| `aria-selected` | On each `tab` | `"true"` on the active tab; `"false"` on others |
| `aria-controls` | On each `tab` | References the controlled `tabpanel`'s id |
| `aria-labelledby` | On each `tabpanel` | References the controlling `tab`'s id |
| `tabindex` | On each `tab` | Active tab: `0`; inactive tabs: `-1` (roving tabindex pattern) |
| `aria-disabled` | On disabled `tab` (rare) | `"true"`; still receives roving tabindex |

**Single-tab focus.** Only ONE tab in the tablist is in the document tab
order at any time — the currently selected tab. Other tabs are reachable
via arrow keys but NOT via Tab. This is the "roving tabindex" pattern.

**WCAG citations:**
- SC 1.3.1 Info and Relationships — the role triad exposes the pattern.
- SC 4.1.2 Name, Role, Value.

---

## 3. ARIA wiring

```html
<div role="tablist" aria-label="Property details">
  <button role="tab" id="tab-overview" aria-selected="true" aria-controls="panel-overview" tabindex="0">
    Overview
  </button>
  <button role="tab" id="tab-leases" aria-selected="false" aria-controls="panel-leases" tabindex="-1">
    Leases
  </button>
</div>

<div role="tabpanel" id="panel-overview" aria-labelledby="tab-overview">
  Overview content
</div>
<div role="tabpanel" id="panel-leases" aria-labelledby="tab-leases" hidden>
  Leases content
</div>
```

### Panel visibility

Inactive tabpanels are hidden via the `hidden` HTML attribute (NOT
`display: none` via CSS, which fails on some SR implementations that
walk the DOM). The Radix implementation uses `data-state="active"` /
`"inactive"` and CSS rules that hide via `display: none` for inactive
state; both approaches are acceptable per WAI-ARIA authoring practices —
contract pins `hidden` attribute as the canonical default for forward-
spec implementation, with the `data-state` approach allowed when the
implementation library prefers it.

**WCAG citation:** SC 1.3.1 Info and Relationships.

---

## 4. Automatic vs. manual activation

WAI-ARIA 1.2 offers two activation modes:

### 4.1 Automatic activation (default)

Pressing arrow keys MOVES focus AND ACTIVATES the focused tab (`aria-
selected` flips immediately + panel changes).

**Pros:** Fewer keystrokes for sighted users; SR users get immediate
content updates.

**Cons:** If a tab's panel triggers an expensive operation (load data
from the network), every arrow-key press triggers the operation. Causes
"thrash" on slow connections.

### 4.2 Manual activation

Arrow keys MOVE focus only. Space or Enter ACTIVATES the focused tab.

**Pros:** No thrash on slow panels; SR user can preview tab labels
without committing.

**Cons:** Two keystrokes per change.

### 4.3 Choice rule

The contract DEFAULT is automatic activation. Switch to manual when:

- Tab activation triggers a network request.
- Tab panels have heavy initialization (charts, large lists, video).

Decision is per-instance via prop (`activationMode?: 'automatic' |
'manual'`); the Semantic contract owns the prop signature.

**WCAG citation:** SC 2.1.1 Keyboard.

---

## 5. Keyboard

For `aria-orientation="horizontal"` (default):

| Key | Behaviour |
|---|---|
| Tab | Move focus INTO the tablist (lands on the active tab) OR move focus OUT to the next focusable AFTER the tablist (typically inside the active tabpanel) |
| Shift+Tab | Reverse Tab |
| Right Arrow | Move focus to next tab; activate immediately in automatic mode |
| Left Arrow | Move focus to previous tab; activate immediately in automatic mode |
| Home | Move focus to first tab; activate immediately in automatic mode |
| End | Move focus to last tab; activate immediately in automatic mode |
| Space / Enter | Activate the focused tab (manual mode); no-op in automatic mode (already activated by arrow) |
| Down / Up arrows | NOT bound at horizontal orientation |

For `aria-orientation="vertical"`:

| Key | Behaviour |
|---|---|
| Down Arrow | Move focus to next tab; activate (automatic) |
| Up Arrow | Move focus to previous tab; activate (automatic) |
| Home / End / Space / Enter | Same as horizontal |
| Left / Right arrows | NOT bound at vertical orientation |

**Arrow-key wrap.** Right (or Down) from the last tab wraps to the first.
Left (or Up) from the first wraps to the last. This is WAI-ARIA
convention; the Radix implementation honors it.

**Disabled tabs.** Arrow keys SKIP disabled tabs (focus moves past them
to the next enabled tab). However, Home / End MAY land on disabled tabs
since the user explicitly targeted "first" or "last" — implementation
choice; Radix skips disabled here too.

**WCAG citations:**
- SC 2.1.1 Keyboard.
- SC 2.4.3 Focus Order.

---

## 6. Focus management

### 6.1 Roving tabindex

At any time, exactly ONE tab in the tablist has `tabindex="0"`; all others
have `tabindex="-1"`. The `tabindex="0"` tab is the currently selected
tab. When arrow keys move focus, the implementation updates the
`tabindex` values in tandem so Tab from outside the list lands on the
NEWLY selected tab.

### 6.2 Focus-visible

`focus-visible:` styling — pointer-focus does NOT show the ring; keyboard
focus does. The active tab is visually distinct via `aria-selected` styling
even without the focus ring; the focus ring distinguishes "selected AND
keyboard-focused" from "selected but pointer-focused".

### 6.3 Focus after panel content updates

When a tab activates and the panel renders new content, focus stays on
the tab. The user can Tab forward to enter the panel content. The
implementation MUST NOT auto-focus the panel content (that would be
disorienting + violate SC 3.2.2 On Input).

**WCAG citations:**
- SC 2.4.7 Focus Visible.
- SC 2.4.13 Focus Appearance.
- SC 3.2.2 On Input — auto-focus would be a context change.

---

## 7. Color contrast

Per [TabStrip.Styling §2](./TabStrip.Styling.md) and the proposed
`navigation.tokens.json` defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Tab foreground (every state) on tab background | 4.5:1 | SC 1.4.3 |
| Active-tab indicator (underline / pill bg) on container bg | 3:1 | SC 1.4.11 |
| Tablist border-bottom on container bg | 3:1 | SC 1.4.11 |
| Focus ring on tab AND on surrounding surface | 3:1 | SC 1.4.11 / SC 2.4.13 |

Provider overrides MUST re-verify.

**Color is not the only channel.** The active-tab signal carries via
THREE channels: `aria-selected` (programmatic), foreground colour (visual
1), and indicator stripe / pill bg (visual 2 — shape-based).

**WCAG citation:** SC 1.4.1 Use of Color.

---

## 8. Touch target

Tab touch targets MUST be ≥ 24×24 CSS pixels per WCAG 2.2 SC 2.5.8. The
default `min-height` per size: sm = 32px, md = 40px, lg = 44px. The
horizontal target depends on label length (typically far exceeds 24px).

For `sm` size with very short labels (e.g., single-word tabs of 4-5
chars), verify the rendered width meets 24px or rely on the Spacing
Exception (tabs are adjacent and tend to satisfy spacing requirements).

**WCAG citation:** SC 2.5.8 Target Size (Minimum).

---

## 9. Reduced motion

If the indicator slides between tabs on selection change (Radix default),
the transition MUST honor `prefers-reduced-motion: reduce` (see Styling
§6).

**WCAG citation:** SC 2.3.3 Animation from Interactions.

---

## 10. Do / Don't

### Do

- Use the `tablist` / `tab` / `tabpanel` role triad.
- Provide `aria-label` (or `aria-labelledby`) on the tablist.
- Use roving tabindex (`0` on active, `-1` on others).
- Bind arrow keys for in-list navigation; bind Tab to enter/exit.
- Wire `aria-controls` (tab → panel) AND `aria-labelledby` (panel → tab).
- Pick automatic OR manual activation based on panel-load cost.
- Skip disabled tabs in arrow-key navigation.
- Use `hidden` attribute (or `data-state`) to hide inactive panels.

### Don't

- Don't put tabs in the document Tab order — only the active tab is in
  tab order. Inactive tabs use `tabindex="-1"` + arrow-key reach.
- Don't use colour alone for active tab — pair with `aria-selected` +
  indicator stripe.
- Don't auto-focus the panel content when a tab activates — that's a
  context change (SC 3.2.2).
- Don't use `display: none` via CSS-only to hide inactive panels —
  prefer the `hidden` attribute for SR-compat.
- Don't bind Tab as the in-list navigation key — that breaks the
  established WAI-ARIA pattern.

---

## 11. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** Radix Tabs primitives;
  composition with brand styling.
- **Web Components (Phase M4, Lit):** Vaadin Tabs is the WC reference;
  ARIA emissions are framework-neutral.

---

## 12. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | Role triad emitted correctly: `tablist` on container, `tab` on buttons, `tabpanel` on panels | Snapshot test |
| F2 | Roving tabindex: exactly one tab has `tabindex="0"`; others have `-1` | Snapshot test + E2E |
| F3 | Arrow-key navigation works in horizontal orientation; Up/Down works in vertical | E2E test |
| F4 | Disabled tabs skipped during arrow-key navigation | E2E test |
| F5 | Automatic mode activates on arrow; manual mode requires Space/Enter | E2E test in both modes |
| F6 | Arrow-key wrap (Right from last → first; Left from first → last) | E2E test |
| F7 | `aria-controls` and `aria-labelledby` correctly cross-reference between tabs and panels | Snapshot test |
| F8 | Inactive panels are `hidden` (NOT just `display:none`); active panel is not | Snapshot test + manual SR test |
| F9 | Focus stays on tab after activation (NOT auto-moved into panel) | E2E test |
| F10 | `prefers-reduced-motion` suppresses indicator slide | Manual test with reduce-motion on |
| F11 | Contrast verification for tabFg/tabBg every variant × every state | Run axe scan |
| F12 | Active-tab signal redundancy: aria-selected + foreground + indicator ALL present | Manual SR + visual regression |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #131 TabStrip (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- WAI-ARIA Authoring Practices Guide — Tabs pattern: https://www.w3.org/WAI/ARIA/apg/patterns/tabs/
- Radix Tabs — primary React foundation
- Vaadin Tabs — WC foundation
- [TabStrip.Semantic.md](./TabStrip.Semantic.md) — prop contract
- [TabStrip.Interaction.md](./TabStrip.Interaction.md) — behavioural contract
- [TabStrip.Styling.md](./TabStrip.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `tablist`, `tab`, `tabpanel`, `aria-selected`, `aria-controls`, `aria-orientation`, `aria-labelledby`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.4.13 Focus Appearance
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
