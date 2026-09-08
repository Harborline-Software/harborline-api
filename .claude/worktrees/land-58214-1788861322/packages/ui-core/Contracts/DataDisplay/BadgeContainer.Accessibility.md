# BadgeContainer — Accessibility Contract

- **Component:** BadgeContainer
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Draft
- **Companion contracts:** [Semantic](./BadgeContainer.Semantic.md) · [Interaction](./BadgeContainer.Interaction.md) · [Styling](./BadgeContainer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/BadgeContainer.tsx`

---

## 1. Purpose

`BadgeContainer` is a presentational positioning wrapper. Its accessibility
contract is narrow and mostly about **boundaries**: the wrapper adds no role,
is never focusable, and does not announce anything. Accessibility
responsibility lives in (a) the wrapped interactive element passed as
`children` and (b) the overlay `Badge`. This contract pins those boundaries so
consumers do not expect the wrapper to carry name, role, or focus.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA 1.2
authoring practice.

---

## 2. Roles & ARIA wiring

| Element | Role | Notes |
|---|---|---|
| Wrapper `<div>` | **NONE** (no role attribute) | A bare positioning `<div>` carries no implicit role; the wrapper is invisible to the accessibility tree as a semantic element |
| `aria-label` | NOT default | The wrapper has no accessible name of its own — naming belongs to the wrapped control |
| `aria-hidden` | NOT set | The wrapper must NOT hide its subtree; the wrapped control and the overlay `Badge` must remain reachable |
| `...rest` passthrough | host-supplied | Any `id` / `data-*` / `aria-*` / handlers the host spreads via `...rest` forward verbatim to the `<div>` (per [Semantic §2](./BadgeContainer.Semantic.md)). The wrapper does not override them |

**WCAG citation:** SC 4.1.2 Name, Role, Value — the wrapper contributes no
name/role; the accessible name of the composition comes from the wrapped
control (and any `aria-label` the host puts on it), not from the container.

---

## 3. Accessible name

`BadgeContainer` has no accessible name and supplies none. In the canonical
composition — an icon button wrapped with a count `Badge` — the **button**
owns the accessible name, typically composed to include the badge value:

```jsx
<BadgeContainer>
  <button aria-label={`Notifications, ${count} unread`}>
    <BellIcon aria-hidden="true" />
  </button>
  <Badge variant="danger">{count}</Badge>
</BadgeContainer>
```

The wrapper neither adds to nor subtracts from this name.

---

## 4. Keyboard & focus

The wrapper is **not focusable** and binds no keys.

| Key | Behaviour | Where handled |
|---|---|---|
| Tab / Shift+Tab | Passes through — focus lands on focusable descendants (the wrapped control) in DOM order | Wrapped control |
| Enter / Space | Not handled | Wrapped control / `Badge` |

The wrapper MUST NOT set `tabindex` on itself and MUST NOT alter the tab order
of its descendants (per [Interaction §2–§3](./BadgeContainer.Interaction.md)).

**WCAG citation:** SC 2.1.1 Keyboard — `BadgeContainer` contributes no keyboard
surface; the wrapped interactive element carries the keyboard contract.

---

## 5. Focus management

None. The wrapper never receives focus and never moves focus on mount/unmount.
If a `Badge` appears/disappears inside it (e.g. a count goes 0 → 3), focus does
**not** move to the new badge — any announcement is the `Badge`'s opt-in
live-region responsibility (see §6), not the container's.

**WCAG citation:** SC 3.2.2 On Input — the wrapper causes no context change.

---

## 6. The overlay-Badge boundary

Dynamic-value announcement (e.g. an unread count that changes during the
session) is the overlay `Badge`'s responsibility via its opt-in
`aria-live="polite"` region — see [Badge.Accessibility §6](./Badge.Accessibility.md).
`BadgeContainer` provides no live region and must not wrap its subtree in one
(that would double-announce the wrapped control). Colour-independence for a
status-dot `Badge` is likewise the `Badge`'s contract
([Badge.Accessibility §4](./Badge.Accessibility.md), SC 1.4.1), not the
container's.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BC1 | None | The wrapper is intentionally semantics-free; there is no a11y debt at the container level | N/A — by design |

The only accessibility risks in this composition live in the **wrapped control**
(must have an accessible name + the correct role) and the **overlay `Badge`**
(must carry text/aria for any state it conveys). Both are governed by their own
contracts.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/badges/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
