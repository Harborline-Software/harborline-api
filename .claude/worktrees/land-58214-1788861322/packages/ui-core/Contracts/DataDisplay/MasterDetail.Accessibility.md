# MasterDetail — Accessibility Contract

- **Component:** MasterDetail
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MasterDetail.Semantic.md) · [Interaction](./MasterDetail.Interaction.md) · [Styling](./MasterDetail.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 MasterDetail (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin Details baseline)

---

## 1. Master list

`role="listbox"` with `aria-label="Items list"` (or caller-provided label). Selected item has `aria-selected="true"`.

---

## 2. Master list items

`role="option"` with `aria-selected` reflecting selection state.

---

## 3. Detail panel

`role="region"` with `aria-label` describing the selected item (e.g., `aria-label="Item detail"`). `aria-live="polite"` announces content changes when selection changes.

---

## 4. Relationship

`aria-controls` on the master list pointing to the detail panel region communicates the relationship to AT.

---

## 5. Known gaps

None identified for forward-spec.
