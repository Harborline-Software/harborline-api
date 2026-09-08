# RadioGroup — Interaction Contract

- **Component:** RadioGroup
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadioGroup.Semantic.md) · [Accessibility](./RadioGroup.Accessibility.md) · [Styling](./RadioGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RadioGroup.tsx`
- **Catalog row:** #105 RadioGroup (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Selection behavior

Each `<input type="radio">` fires `onChange` when clicked (unless `isDisabled`). The guard `!isDisabled && onChange(opt.value)` prevents selection on disabled items.

Native browser behavior handles:
- Arrow key navigation between radio buttons (same `name` group)
- Only the selected radio in the group is in the tab order

---

## 2. Disabled states

- `disabled=true` (group-level): all options disabled
- `opt.disabled=true` (item-level): only that option disabled

`isDisabled = disabled || opt.disabled`

---

## 3. Known gaps

None. RadioGroup interaction relies on native radio button behavior.
