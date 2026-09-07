# Transfer — Accessibility Contract

- **Component:** Transfer / ShuttleList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Transfer.Semantic.md) · [Interaction](./Transfer.Interaction.md) · [Styling](./Transfer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 Transfer / ShuttleList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Transfer baseline)

---

## 1. List panels

Each list panel uses `role="listbox"` with `aria-multiselectable="true"`. Panel title is provided via `aria-labelledby`.

---

## 2. List items

Each item uses `role="option"` with `aria-selected={selected}` and `aria-disabled={disabled}`.

---

## 3. Transfer buttons

Transfer buttons have descriptive `aria-label` values: `"Move selected items right"` / `"Move selected items left"`. Disabled when no items are selected in the corresponding panel.

---

## 4. Select-all checkbox

Standard `<input type="checkbox">` with `aria-label="Select all items"` and `indeterminate` state.

---

## 5. Search input

`aria-label="Search list"` (or localized equivalent) on the search input in each panel.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TRF-A1 | Medium | Complex multi-panel interaction pattern — keyboard-only flow (select → Tab to button → activate) needs explicit documentation in AT instructions | Accepted-risk M1 |
