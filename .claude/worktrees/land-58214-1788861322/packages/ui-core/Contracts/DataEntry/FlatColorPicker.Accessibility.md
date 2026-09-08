# FlatColorPicker — Accessibility Contract

- **Component:** FlatColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FlatColorPicker.Semantic.md) · [Interaction](./FlatColorPicker.Interaction.md) · [Styling](./FlatColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FlatColorPicker.tsx`
- **Catalog row:** #62 FlatColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Tab bar `<div>` | No role |
| `<button type="button">` | View tabs | Keyboard focusable |
| *(delegated)* | ColorGradient view | See ColorGradient accessibility contract |
| *(delegated)* | ColorPalette view | See ColorPalette accessibility contract |
| `<button type="button">` | Apply button | No aria-label; text "Apply" is label |
| `<button type="button">` | Cancel button | No aria-label; text "Cancel" is label |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FCP2 | Medium | Tab buttons lack `role="tab"`, `aria-selected`, `aria-controls` — not ARIA tabpanel pattern | Accepted-risk M1; buttons are keyboard-accessible even without full ARIA tab role |
