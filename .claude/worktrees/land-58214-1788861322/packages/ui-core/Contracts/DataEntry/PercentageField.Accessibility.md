# PercentageField — Accessibility Contract

- **Component:** PercentageField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PercentageField.Semantic.md) · [Interaction](./PercentageField.Interaction.md) · [Accessibility](./PercentageField.Accessibility.md) · [Styling](./PercentageField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PercentageField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PercentageField shares its ARIA surface pattern with NumericInput (unified
bordered wrapper, label, hint/error). The main difference is no increment/
decrement buttons and a fixed `%` suffix adornment.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Outer `<div>` | (none) | Flex column container |
| `<label>` (when `label` supplied) | (linked) | Visible; `htmlFor={id}` |
| Inner bordered wrapper `<div>` | (none) | Focus-within ring target |
| `<input type="number">` | implicit `spinbutton` | `aria-invalid`, `aria-describedby` |
| `%` suffix `<span>` | (decorative) | No ARIA role |
| Hint `<p>` | (none) | `id="{id}-hint"` |
| Error `<p>` | `role="alert"` | Live region |

---

## 3. Label linkage

When `label` is provided, `<label htmlFor={id}>` renders visibly.

> **Gap A-1:** When used inside a FormField, PercentageField reads `ctx?.id`
> but the FormField label's `htmlFor` must also match. Since PercentageField
> doesn't use `useFormField()` for label text, the label linkage works only
> when the FormField's `name` matches the `id` used by this component.

---

## 4. `aria-describedby`

When `hint` is provided: `aria-describedby="{id}-hint"` set on the input.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 3.3.2.

---

## 5. Error state — `aria-invalid`

When `error` is truthy:

```tsx
aria-invalid={hasError || undefined}
```

`<p role="alert">` fires the error message as a live region.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 4.1.2.

---

## 6. Suffix `%` adornment

The `%` suffix span has no `aria-label` or `aria-hidden`. AT may announce
it as "percent" text content when focus is within the wrapper — this is
acceptable since it provides context.

---

## 7. Disabled state

Via prop spread: native `disabled` on the input. Wrapper: `pointer-events-none
opacity-50 bg-gray-50`.

---

## 8. Focus ring

The inner wrapper uses `focus-within:ring-2 focus-within:ring-blue-500`
(or `ring-red-500` in error state). Same pattern and gap as NumericInput
(see NumericInput.Accessibility §8 Gap A-3 — per-element vs focus-within ring).

---

## 9. `min` / `max` on native input

`min={min}` and `max={max}` are set on the native `<input>`. AT (screen readers
that support spinbutton) may announce the range bounds.

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Label linkage depends on `id` coordination with FormField | WCAG SC 1.3.1 | Use `useFormField()` for `id` + label consumption |
| A-2 | `focus-within` ring instead of per-element `focus-visible` | WCAG SC 2.4.7 | Add `focus-visible:ring-2` to the input |
| A-3 | Suffix `%` not AT-labeled as a unit | (advisory) | Include "%" or "percent" in label text |
