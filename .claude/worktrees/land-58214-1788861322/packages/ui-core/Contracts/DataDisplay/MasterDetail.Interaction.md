# MasterDetail — Interaction Contract

- **Component:** MasterDetail
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MasterDetail.Semantic.md) · [Accessibility](./MasterDetail.Accessibility.md) · [Styling](./MasterDetail.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 MasterDetail (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin Details baseline)

---

## 1. Item selection

Clicking an item in the master list calls `onSelectionChange(key, item)` and updates `selectedKey`. The corresponding `detailRender` content updates.

---

## 2. Keyboard navigation

Within the master list, `ArrowUp`/`ArrowDown` move selection. `Enter` confirms selection and moves focus to the detail panel. `Escape` in the detail panel moves focus back to the master list.

---

## 3. Deselection

Clicking the currently selected item again deselects it (`onSelectionChange(null, null)`). Detail panel shows empty state.

---

## 4. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
