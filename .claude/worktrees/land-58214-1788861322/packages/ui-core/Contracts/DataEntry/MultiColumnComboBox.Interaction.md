# MultiColumnComboBox — Interaction Contract

- **Component:** MultiColumnComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiColumnComboBox.Semantic.md) · [Accessibility](./MultiColumnComboBox.Accessibility.md) · [Styling](./MultiColumnComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiColumnComboBox.tsx`
- **Catalog row:** #85 MultiColumnComboBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED
  → focus input → OPEN (filter='', activeIdx=-1, editing=true)
  → click chevron button → OPEN

OPEN (showing filtered table)
  → type in input → filter updates; activeIdx resets
  → ArrowDown → activeIdx++
  → ArrowUp → activeIdx--
  → Enter (activeIdx ≥ 0) → select(filtered[activeIdx]); CLOSED
  → click row → select(row); CLOSED (via onMouseDown)
  → Escape → CLOSED
  → blur input → CLOSED after 150ms timeout
```

---

## 2. Selection

`select(row)`:
- If uncontrolled: `setInternal(row[valueField])`
- `onValueChange?.(row[valueField])`
- `setFilter('')`
- `setEditing(false)`
- `setOpen(false)`

---

## 3. Focus / blur

`onFocus`: sets `editing=true`, opens dropdown, clears filter.
`onBlur`: `setTimeout(150ms)` to allow `onMouseDown` on rows to fire before closing (prevents the dropdown from closing before row click is processed).

---

## 4. Chevron button

`tabIndex={-1}` — not in Tab order; clicking opens/closes. When input is focused, Tab does not land on chevron.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MCCB1 | Medium | No click-outside handler — dropdown stays open until Escape, blur, or selection | Accepted-risk M1; blur with 150ms timeout partially handles this |
| G-MCCB2 | Low | `activeIdx` is not reset when filter changes — keyboard-highlighted row can shift to unrelated item | Accepted-risk M1 |
