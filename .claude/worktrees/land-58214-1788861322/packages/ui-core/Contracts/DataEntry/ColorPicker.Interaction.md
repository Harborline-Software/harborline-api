# ColorPicker — Interaction Contract

- **Component:** ColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPicker.Semantic.md) · [Accessibility](./ColorPicker.Accessibility.md) · [Styling](./ColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPicker.tsx`
- **Catalog row:** #31 ColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED
  → click trigger button → OPEN (FlatColorPicker dropdown appears)

OPEN
  → user selects color in FlatColorPicker → select(color) → CLOSED
  → click trigger button → CLOSED (toggle)
```

FlatColorPicker is rendered with `showActions={false}` — selection in the panel fires `onValueChange` and immediately closes the dropdown.

---

## 2. Trigger button

Toggle `open` state on click. When `disabled`, the button is `disabled` (native) and click is blocked.

---

## 3. Color selection

`select(color)`:
- If uncontrolled: `setInternal(color)`
- `onValueChange?.(color)`
- `setOpen(false)`

---

## 4. No close-on-outside-click

In M1, there is no click-outside handler. The dropdown remains open until the user selects a color or clicks the trigger again. Gap G-CPKR1.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPKR1 | Medium | No click-outside / focus-outside handler — dropdown stays open until color is selected or trigger re-clicked | Accepted-risk M1 |
| G-CPKR2 | Low | `format` prop is no-op in M1 — always returns hex | Accepted-risk M1; reserved |
