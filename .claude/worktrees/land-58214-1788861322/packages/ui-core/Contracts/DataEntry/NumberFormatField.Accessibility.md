# NumberFormatField — Accessibility Contract

- **Component:** NumberFormatField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberFormatField.Semantic.md) · [Interaction](./NumberFormatField.Interaction.md) · [Accessibility](./NumberFormatField.Accessibility.md) · [Styling](./NumberFormatField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberFormatField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumberFormatField wraps a native `<input type="text">` with optional prefix/
suffix adornments. It uses `inputMode="decimal"` for mobile. This contract names
the label approach, focus semantics, and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Wrapper `<div>` | (none) | Flex container; receives focus ring classes |
| `<input type="text">` | implicit `textbox` | Controlled input |
| Label `<label>` | (linked) | `sr-only`; linked via `htmlFor={id}` when `label` prop supplied |
| Prefix `<span>` | (decorative) | No ARIA role |
| Suffix `<span>` | (decorative) | No ARIA role |

---

## 3. Label linkage

When `label` is provided, a `<label className="sr-only">` is rendered with
`htmlFor={id}`. This links the input to an accessible label.

When `label` is not provided, a parent FormField labels the input through the
shared context id; standalone hosts must provide `label`.

---

## 4. `aria-describedby`

FormFieldContext `describedBy` is forwarded to the native input. This connects
the field to composed hint/error content.

---

## 5. Validation state

`error={true}` emits `aria-invalid="true"` and destructive border/focus
styling. `required={true}` applies native `required`. Local required/disabled
values combine with FormFieldContext through logical OR.

The composed FormField error uses `role="alert"` for immediate announcement
and remains referenced by `aria-describedby` on focus. Validation prose is
resolved from stable localizable catalog codes; NumberFormatField does not
accept a `validationMessage` string prop.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 3.3.2, SC 4.1.2.

---

## 6. `inputMode`

`inputMode="decimal"` is set. AT reads the field as `textbox`; mobile opens a
decimal keypad.

---

## 7. Prefix / suffix labels

Prefix and suffix spans carry no `aria-label` or `aria-hidden`. AT may read
their text content. This is acceptable for short labels (`"$"`, `"%"`) but can
be confusing for longer strings.

> **Gap A-4:** Prefix/suffix text is not consistently associated with the input
> for AT. Hosts should include relevant units in the FormField label text (e.g.,
> "Amount (USD)").

---

## 8. Focus ring

The wrapper `<div>` receives the focus ring classes (not the input itself),
because the input uses `outline-none`:

```
border-blue-500 ring-2 ring-blue-500/20
```

`ring-2` satisfies WCAG 2.4.7 and 2.4.13 for the wrapper element.

> **Gap A-5:** The focus indicator is on the wrapper `<div>`, not the `<input>`.
> Some AT / browser combinations may not correctly surface the wrapper's
> focus-visible ring when the input inside has `focus:outline-none`. Verify
> with NVDA/JAWS that the focus ring is announced.

---

## 9. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-4 | Prefix/suffix not AT-associated | WCAG SC 1.3.1 | Document host responsibility for unit in label |
| A-5 | Focus ring on wrapper div, not input | WCAG SC 2.4.7 | Verify cross-AT; consider moving ring to input |
