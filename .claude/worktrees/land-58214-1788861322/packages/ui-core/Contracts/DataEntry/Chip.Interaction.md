# Chip — Interaction Contract

- **Component:** Chip
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chip.Semantic.md) · [Accessibility](./Chip.Accessibility.md) · [Styling](./Chip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Chip.tsx`
- **Catalog rows:** #25 Chip · #26 ChipList
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Chip state machine

```
UNSELECTED (selected=false)
  → click (not disabled) → SELECTED
  → remove click → onRemove() (no state change)

SELECTED (selected=true)
  → click (not disabled) → UNSELECTED
  → remove click → onRemove() (no state change)
```

---

## 2. Selection toggle

Clicking the Chip body toggles `selected`. Remove button (`×`) fires `onRemove()` with `e.stopPropagation()` — remove click does NOT trigger the toggle.

Disabled chips: `if (disabled) return` in both handlers — no state change, no callbacks.

---

## 3. ChipList selection modes

| Mode | Behaviour |
|---|---|
| `none` | No selection; each Chip renders without `selected` prop |
| `single` | Selecting a chip deselects all others; clicking the selected chip deselects it |
| `multiple` | Each chip toggles independently; multiple chips can be selected |

---

## 4. Controlled vs uncontrolled

**Chip**: `selected` prop = controlled; `defaultSelected` = uncontrolled seed.

**ChipList**: `value` prop = controlled; `defaultValue` = uncontrolled seed.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CH1 | Low | Remove button uses `role="button"` on a `<span>` rather than a `<button>` | Accepted-risk M1; functional but non-ideal semantics |
