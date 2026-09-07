# MaskedTextBox — Interaction Contract

- **Component:** MaskedTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedTextBox.Semantic.md) · [Accessibility](./MaskedTextBox.Accessibility.md) · [Styling](./MaskedTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MaskedTextBox.tsx`
- **Catalog row:** #82 MaskedTextBox (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input processing

```
user types → onChange fires
  raw = e.target.value.replace(/\D/g, '')   // strip non-digits
  masked = applyMask(raw, mask, promptChar)
  if uncontrolled: setInternal(raw)
  onChange?.(masked, raw)
```

The component re-displays `displayed = applyMask(rawValue, mask, promptChar)`.

---

## 2. Empty state

When `displayed === maskPlaceholder` (all positions unfilled), the input shows `value=""` and displays the `placeholder` attribute instead. This prevents the mask itself from showing as a value when empty.

---

## 3. Readonly

When `readonly=true`: `readOnly` on the input — user cannot type or edit.

---

## 4. Keyboard

Standard text input keyboard behavior. No special handling — mask is applied on every change event, not keystroke-by-keystroke. Cursor position is not managed after masking; this is a known limitation.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MTB1 | Medium | Cursor position jumps to end after each keystroke (masked value replaces full input on `onChange`) | Accepted-risk M1; cursor management requires `selectionStart/End` tracking |
| G-MTB2 | Medium | `L` and `A` mask tokens not enforced — only digits are extracted from user input | Accepted-risk M1; documented in Semantic §3 |
