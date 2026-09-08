# MultiSelectTree — Interaction Contract

- **Component:** MultiSelectTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelectTree.Semantic.md) · [Accessibility](./MultiSelectTree.Accessibility.md) · [Styling](./MultiSelectTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelectTree.tsx`
- **Catalog row:** #87 MultiSelectTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED
  → click trigger → OPEN

OPEN
  → click node checkbox → toggle(node.value)
  → click Select All checkbox → toggleAll()
  → click trigger → CLOSED
  → click outside → CLOSED (no outside handler — Gap G-MST1)
```

Dropdown stays open while checking items — does not close on selection.

---

## 2. `toggle(val)`

```
next = current.includes(val) ? current.filter(v => v !== val) : [...current, val]
if uncontrolled: setInternal(next)
onValueChange?.(next)
```

---

## 3. `toggleAll()`

```
enabledValues = allItems (flattened) where !item.disabled .map(item.value)
allSelected = enabledValues.every(v => current.includes(v))
next = allSelected ? [] : enabledValues
```

---

## 4. Node expand/collapse

Each `MultiSelectTreeNode` has independent internal `expanded` state. Click on the expand button (`▸`/`▾`) toggles it. Checkbox and expand button are separate interactive elements.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MST1 | Medium | No click-outside handler — dropdown stays open indefinitely | Accepted-risk M1; user must click trigger again to close |
| G-MST2 | Low | Trigger is a `<div>` not `<button>` — keyboard-only users cannot toggle it | Accepted-risk M1 |
