# NumberStepper — Accessibility Contract

- **Component:** NumberStepper
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberStepper.Semantic.md) · [Interaction](./NumberStepper.Interaction.md) · [Accessibility](./NumberStepper.Accessibility.md) · [Styling](./NumberStepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberStepper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumberStepper renders a `<input type="number">` flanked by two `<button>`
elements. The input acts as a `spinbutton`. The buttons have `aria-label`.
This contract names the ARIA surface and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Root inline-flex `<div>` | (none) | No group role |
| `<input type="number">` | implicit `spinbutton` | `min`, `max`, `step` attributes drive AT range announcement |
| Decrement `<button>` | `button` | `aria-label="Decrease"` |
| Increment `<button>` | `button` | `aria-label="Increase"` |
| Label `<label>` (optional) | (linked) | `sr-only`; linked via `htmlFor={id}` |

---

## 3. Input label

When `label` is provided, `<label className="sr-only">` is rendered with
`htmlFor={id}`. AT announces the label on input focus.

When `label` is absent (common usage), the input has no label.

> **Gap A-1:** When `label` is absent, the `<input>` carries no label and AT
> will announce it as an unlabeled spinbutton. Hosts MUST supply `label` or
> use a parent FormField with matching `name` for label linkage.

---

## 4. `aria-label` on buttons

Decrement: `aria-label="Decrease"`. Increment: `aria-label="Increase"`.
These are short but functional. A more descriptive label (e.g., "Decrease
quantity") would improve context for AT users.

---

## 5. `aria-describedby`

Not wired. No `describedBy` from FormFieldContext.

> **Gap A-2:** No `aria-describedby`. Hint/error from parent FormField not
> announced.

---

## 6. Error state

No `aria-invalid`. No `error` prop.

> **Gap A-3:** No error surface for AT.

---

## 7. Disabled state

Native `disabled` on all three elements. AT announces each as "unavailable".

---

## 8. Boundary disabling

When `value <= min`, decrement button has native `disabled`. When `value >= max`,
increment button has native `disabled`. AT announces these buttons as "dimmed"
or "unavailable". The user understands the current bound has been reached.

---

## 9. Focus ring

Decrement/Increment buttons:

```
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

Input:

```
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

`ring-2` on all interactive elements. Satisfies WCAG 2.4.7 and 2.4.13.

---

## 10. Touch targets

| Size | Button | Input |
|---|---|---|
| `sm` | 28×28px | 28×48px |
| `md` | 36×36px | 36×56px |
| `lg` | 44×44px | 44×64px |

`lg` buttons (44×44px) meet WCAG 2.2 SC 2.5.8 without exception. `sm` and
`md` rely on spacing exceptions.

---

## 11. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | No label when `label` prop absent | WCAG SC 1.3.1, SC 4.1.2 | Require `label` or use FormField |
| A-2 | No `aria-describedby` | WCAG SC 1.3.1, SC 3.3.2 | Wire `describedBy` from FormFieldContext |
| A-3 | No `aria-invalid` | WCAG SC 3.3.1, SC 4.1.2 | Add `error` prop |
