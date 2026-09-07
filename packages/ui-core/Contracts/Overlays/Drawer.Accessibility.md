# Drawer — Accessibility Contract

- **Component:** Drawer (Sheet)
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Drawer.Semantic.md) · [Interaction](./Drawer.Interaction.md) · [Styling](./Drawer.Styling.md)
- **Related contract:** [Dialog.Accessibility.md](./Dialog.Accessibility.md) — Drawer inherits the modal-overlay accessibility contract from Dialog; this contract names the deltas (edge anchoring, mobile-drawer overlap with SideNav).
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Sheet (built on Radix Dialog)_
- **Catalog row:** #46 Drawer (`app-priority: high`, `library-scope: v1`, `Notes: alias: Sheet (shadcn)`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Drawer is a modal overlay surface. Its accessibility contract is largely
INHERITED from Dialog because both compose on Radix Dialog primitives — the
focus trap, `aria-modal`, Escape-to-close, return-focus, and scroll-lock
behaviours are all identical.

This contract documents:

1. The inheritance from Dialog (most of the contract).
2. The DELTAS specific to Drawer (edge anchoring, swipe-to-dismiss
   alternative, the M2-specific consideration that AppLayout's mobile
   drawer mode uses a similar pattern for SideNav — confirm no namespace
   collision).
3. The initial-focus rule (close button by default, not the first
   interactive child).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Inherited from Dialog

The following are NOT re-stated in detail — see Dialog.Accessibility §2-12:

| Concern | Inherited from Dialog |
|---|---|
| Root role | `role="dialog"` with `aria-modal="true"` |
| Accessible name | `aria-labelledby` pointing to title id |
| Accessible description | `aria-describedby` pointing to description id (optional) |
| Focus trap | Focus is trapped inside drawer panel while open |
| Initial focus on open | First focusable inside drawer (default; see §4 for Drawer-specific rule) |
| Escape to close | Required; returns focus to opener |
| Return focus | On close, focus returns to the element that opened the drawer |
| Scroll lock | Body scroll locked while drawer is open |
| Background `inert` | Backround regions emit `inert` (or `aria-hidden="true"`) while drawer is open |

Drawer composes on Radix Dialog (via shadcn Sheet), which implements all
of these by default.

**WCAG citations (inherited):**
- SC 2.1.1 Keyboard.
- SC 2.1.2 No Keyboard Trap.
- SC 2.4.3 Focus Order.
- SC 2.4.11 Focus Not Obscured (Minimum).
- SC 3.2.1 On Focus.
- SC 4.1.2 Name, Role, Value.

---

## 3. Deltas from Dialog

### 3.1 Edge anchoring (vs. centring)

Visually, Drawer slides in from an edge while Dialog appears centred.
The accessibility implementation is identical (both use Radix Dialog
primitives + portal); the difference is purely CSS positioning + the
slide-in animation direction.

### 3.2 Swipe-to-dismiss (mobile, OPTIONAL)

Mobile drawers (bottom-anchored) MAY support swipe-to-dismiss gestures
(swipe-down to close a bottom drawer; swipe-right to close a right-side
drawer on mobile, etc.). When implemented:

| Requirement | Emission |
|---|---|
| Single-pointer alternative | REQUIRED — the close button + Escape + scrim-click MUST all work. Swipe MUST NOT be the only path |
| Reduced motion | Swipe animation MUST honor `prefers-reduced-motion: reduce` per Styling §6 |

**WCAG citation:** WCAG 2.2 SC 2.5.7 Dragging Movements — drag-only
interactions are forbidden; alternatives are mandatory.

### 3.3 Resizable drawer (OUT OF M2 SCOPE)

Drag-to-resize handles on the inner edge are a M3 enhancement. When
introduced, they require:

- Keyboard alternative (arrow keys to resize; Enter to confirm).
- Touch-target ≥ 24×24 on the handle.
- Accessible name on the handle ("Resize drawer").

OUT OF M2 SCOPE — documented for the contract's awareness.

---

## 4. Initial focus on open — Drawer-specific rule

The Dialog Accessibility contract defaults to "first focusable inside
dialog". For Drawer, the recommended default differs:

| Drawer use case | Recommended initial focus |
|---|---|
| Detail-view side panel (read mostly, minimal interaction) | Close button — so the user's first interaction is "exit if opened by mistake" |
| Form drawer (multi-field) | First form field — user is here to fill it out |
| Filter drawer (list of checkboxes) | First filter — user is here to choose |
| Settings sheet | Close button (sheet is exploratory; user may dismiss after browsing) |

**Implementation.** Drawer accepts an `initialFocus` prop:

- `'close'` (default for detail-view drawers; the Drawer contract's M2
  default).
- `'first'` (first focusable inside body).
- `<ref>` (custom focus target supplied by consumer).

The forward-spec implementation MUST default to `'close'` UNLESS the
drawer is composed via `<Drawer.Form>` or similar prop-pattern hint,
where `'first'` is more appropriate.

**WCAG citation:** WCAG 2.2 SC 3.2.1 On Focus — the focus motion on open
is intentional user action, but the destination matters for UX
acceptability.

---

## 5. Coordination with SideNav mobile drawer mode

SideNav.Accessibility §6 documents a mobile-drawer mode that uses an
analogous focus-trap pattern. Both are MODAL OVERLAY surfaces but they
are SEPARATE components.

| Concern | Drawer | SideNav drawer mode |
|---|---|---|
| Use case | Arbitrary task-flow content | Navigation tree (left rail) |
| Trigger | Programmatic from a button / link | AppBar hamburger toggle |
| Opener role | varies | AppBar hamburger button |
| `aria-label` on the panel | varies ("Property details" / "Filter properties" / etc.) | "Main" |
| Initial focus | Close button (this contract's default) | Close button (per SideNav §6.2) |
| Side | configurable (right / left / top / bottom) | always left |

**No conflict.** A page MAY have a SideNav drawer AND a content Drawer
open simultaneously only if both modal contracts coordinate (the
top-most one owns the focus trap; the underneath one becomes `inert`).
The implementation library (Radix) handles nested-dialog scenarios; the
contract documents that nesting is possible but discouraged.

---

## 6. Color contrast

Per [Drawer.Styling §2](./Drawer.Styling.md) and proposed
`overlays.tokens.json` defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Title fg on content-bg | 4.5:1 | SC 1.4.3 |
| Description fg on content-bg | 4.5:1 | SC 1.4.3 |
| Body text on content-bg | 4.5:1 | SC 1.4.3 |
| Header / footer divider on content-bg | 3:1 | SC 1.4.11 |
| Close-button glyph on header-bg | 3:1 | SC 1.4.11 |
| Focus ring on close-button + on content-bg | 3:1 | SC 1.4.11 / SC 2.4.13 |
| Backdrop sufficiency (visual darkening of background) | 3:1 (informational; the backdrop's purpose is to dim, not to carry text contrast) | SC 1.4.11 |

The default token values MUST be pre-verified. Provider overrides MUST
re-verify.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 7. Reduced motion

Slide-in + slide-out + backdrop-fade animations MUST honor
`prefers-reduced-motion: reduce` (see Styling §6). The accessibility
contract (focus trap, return-focus, etc.) is UNCHANGED — only the
visual transition is suppressed.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 8. Touch target

| Element | Min target |
|---|---|
| Close button | ≥ 32×32 per Styling §2.5; falls under 44×44 best-practice but exceeds 24×24 strict minimum |
| Body interactive children | each child carries its own touch-target obligation per its own contract |
| Resizable handle (M3 future) | ≥ 24×24 |

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 9. Keyboard

| Key | Behaviour |
|---|---|
| Tab | Focus traverses focusable elements inside drawer in DOM order; from last wraps to first (focus trap) |
| Shift+Tab | Reverse traversal; from first wraps to last |
| Escape | Closes drawer; focus returns to opener |
| Enter / Space | Activates whichever focused element supports it (inherited from each focusable child) |

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; SC 2.1.2 No Keyboard Trap.

---

## 10. Do / Don't

### Do

- Compose on Radix Dialog (via shadcn Sheet) — inherits focus trap +
  Escape + return-focus + `aria-modal` correctly.
- Provide `aria-labelledby` pointing to the drawer title id.
- Default initial focus to the close button for detail/exploratory
  drawers; default to first focusable for form drawers.
- Provide a close button — even if Escape + scrim work, the visible
  close affordance is mandatory.
- Lock body scroll while drawer is open.
- Apply `inert` to background regions while drawer is open.
- Honor `prefers-reduced-motion` on slide + backdrop fade.
- Provide a single-pointer dismiss alternative if swipe-to-dismiss is
  implemented.

### Don't

- Don't auto-focus a destructive control (e.g., "Delete" button) on
  drawer open. Pick a safe initial focus.
- Don't nest drawers more than one deep. SR users get disoriented.
- Don't omit the close button. Escape + scrim are necessary but not
  sufficient.
- Don't make swipe-to-dismiss the ONLY dismiss path on mobile.
- Don't put `aria-modal="true"` AND `role="dialog"` BOTH on the panel
  AND also on a wrapper — Radix puts them on the panel only.

---

## 11. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Sheet on Radix Dialog.
- **Web Components (Phase M4, Lit):** custom WC built on the platform's
  native `<dialog>` element (with side anchoring via CSS); tokens are
  framework-neutral.

---

## 12. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | `role="dialog"` + `aria-modal="true"` on drawer panel | Snapshot test |
| F2 | `aria-labelledby` correctly points to title id | Snapshot test |
| F3 | Focus is trapped inside drawer while open | E2E test (Tab traversal stays inside) |
| F4 | Escape closes drawer; focus returns to opener | E2E test |
| F5 | Scrim click closes drawer; focus returns to opener | E2E test |
| F6 | Initial focus defaults to Close button (per this contract's M2 default) | E2E test; consumer can override via `initialFocus` prop |
| F7 | Body scroll locked during drawer-open (iOS Safari included) | Manual mobile test |
| F8 | `inert` applied to background while drawer is open | Snapshot test |
| F9 | Swipe-to-dismiss (if implemented) has dismiss-button fallback | Manual mobile test |
| F10 | `prefers-reduced-motion` suppresses slide + backdrop fade | Manual test with reduce-motion on |
| F11 | Nested-drawer scenario behaves correctly (focus trap on innermost; outer is inert) | E2E test if nesting is permitted |
| F12 | Contrast verification for all four sides + all sizes | Run axe scan |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #46 Drawer / Sheet (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Sheet — primary React foundation
- Radix Dialog — underlying primitive (focus trap, ARIA emissions)
- [Drawer.Semantic.md](./Drawer.Semantic.md) — prop contract
- [Drawer.Interaction.md](./Drawer.Interaction.md) — behavioural contract
- [Drawer.Styling.md](./Drawer.Styling.md) — token surface + visual states
- [Dialog.Accessibility.md](./Dialog.Accessibility.md) — INHERITED contract for modal-overlay semantics
- [SideNav.Accessibility.md](../Navigation/SideNav.Accessibility.md) §6 — SideNav drawer mode (related but distinct component)
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `dialog`, `aria-modal`, `aria-labelledby`, `aria-describedby`
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum)
- WCAG 2.2 SC 2.4.13 Focus Appearance
- WCAG 2.2 SC 2.5.7 Dragging Movements
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 4.1.2 Name, Role, Value
