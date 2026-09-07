# DropDownTree — Interaction Contract

- **Component:** DropDownTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownTree.Semantic.md) · [Accessibility](./DropDownTree.Accessibility.md) · [Styling](./DropDownTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownTree.tsx`
- **Catalog row:** #49 DropDownTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED
  → click trigger → OPEN (tree appears)

OPEN
  → click leaf node → select(item); CLOSED
  → click parent node → expand/collapse AND select(item); CLOSED
  → blur dropdown panel (focus leaves) → CLOSED
```

---

## 2. Trigger click

Toggle `open` on click. Trigger `<div>` is not a button (Gap G-DDT1).

---

## 3. Node click

`onClick` on `TreeNode div`: if `item.disabled`, return. If has children, toggle `expanded`. Always calls `onSelect(item)`.

`onSelect` in DropDownTree: `select(item)` → updates state + emits `onValueChange(value, item)` + `setOpen(false)`.

---

## 4. Dropdown close on blur

The dropdown `<div>` has `onBlur`: closes if focus leaves the container (`!e.currentTarget.contains(e.relatedTarget)`).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDT1 | Medium | Trigger is a `<div>` not a `<button>` — not natively keyboard focusable | Accepted-risk M1; no keyboard activation of trigger |
| G-DDT2 | Low | Clicking a parent node closes the dropdown even though the selection may be unintentional | Accepted-risk M1; by-design, parent nodes are selectable |
