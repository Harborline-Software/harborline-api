# NumericInput — Accessibility Contract

- **Component:** NumericInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericInput.Semantic.md) · [Interaction](./NumericInput.Interaction.md) · [Accessibility](./NumericInput.Accessibility.md) · [Styling](./NumericInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumericInput renders a `<input type="number">` inside a unified bordered
wrapper with increment/decrement buttons. It includes a visible label, hint,
and error rendered internally. This contract names the ARIA surface and known
gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Wrapper `<div>` (outer) | (none) | Outer flex column container |
| `<label>` (when `label` supplied) | (linked) | Visible; `htmlFor={id}` |
| Wrapper `<div>` (inner bordered) | (none) | Contains decrement, input, increment |
| Prefix `<span>` | (decorative) | Bordered left adornment |
| Decrement `<button>` | `button` | `aria-label="Decrease"` |
| `<input type="number">` | implicit `spinbutton` | `aria-invalid`, `aria-describedby` |
| Increment `<button>` | `button` | `aria-label="Increase"` |
| Suffix `<span>` | (decorative) | Bordered right adornment |
| Hint `<p>` | (none) | `id="{id}-hint"` |
| Error `<p>` | `role="alert"` | Error live region |

---

## 3. Label linkage

When `label` is provided, `<label htmlFor={id}>` renders visibly above the
wrapper. AT announces the label on input focus.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 4.1.2.

---

## 4. `aria-describedby`

When `hint` is provided (and no error), `aria-describedby="{id}-hint"` is
set on the input. AT announces the hint text when the input is focused.

When `error` is set, hint is suppressed; `aria-describedby` is not set (the
error `<p role="alert">` fires live instead).

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 3.3.2.

---

## 5. Error state — `aria-invalid`

When `error` is truthy:

```tsx
aria-invalid={hasError || undefined}
```

`aria-invalid="true"` announces the input as invalid. The `<p role="alert">`
fires the error message as a live region announcement.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 4.1.2.

---

## 6. Disabled state

The wrapper receives `pointer-events-none` via CSS class. Individual interactive
elements do not receive native `disabled` (except via the spread `disabled`
prop on the input and explicit checks on the buttons).

> **Gap A-1:** When `disabled` is spread via `...props` to the input but not
> explicitly to the buttons, the decrement/increment buttons may remain
> interactive while the input is disabled. Verify that `disabled` is
> consistently propagated to all three interactive elements.

---

## 7. Prefix / suffix

Prefix and suffix are decorative spans with no ARIA role. They are not
announced separately by AT.

> **Gap A-2:** Suffix/prefix text is not AT-associated. Hosts should include
> units in the `label` text (e.g., "Width (m²)").

---

## 8. Focus ring

The inner wrapper uses `focus-within:ring-2 focus-within:ring-blue-500` to
show a ring when any child is focused. The error state uses
`focus-within:ring-red-500`. Individual buttons and the input have no
`focus-visible` classes, relying on the wrapper's `focus-within` ring.

> **Gap A-3:** `focus-within` on the wrapper does not produce a visible ring
> on the specific focused element — it activates whenever any child is focused.
> The focused button or input does not individually show a ring. This can
> confuse AT users who navigate by Tab — the ring "sticks" to the whole wrapper
> even when focus is on a button. Add `focus-visible:ring-2` to individual
> interactive elements for per-element visibility.

---

## 9. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | `disabled` may not propagate consistently to buttons | WCAG SC 4.1.2 | Pass `disabled` explicitly to both buttons |
| A-2 | Prefix/suffix not AT-associated | WCAG SC 1.3.1 | Document in label |
| A-3 | `focus-within` ring instead of per-element `focus-visible` | WCAG SC 2.4.7 | Add `focus-visible:ring-2` to buttons/input |
