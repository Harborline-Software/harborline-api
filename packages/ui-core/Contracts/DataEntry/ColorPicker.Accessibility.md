# ColorPicker — Accessibility Contract

- **Component:** ColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPicker.Semantic.md) · [Interaction](./ColorPicker.Interaction.md) · [Styling](./ColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPicker.tsx`
- **Catalog row:** #31 ColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | Trigger | Native button; keyboard focusable |
| `aria-label={placeholder}` | Trigger | Default: `"Pick a color"` |
| `disabled` | Trigger | When `disabled=true` |
| *(delegated)* | FlatColorPicker | See FlatColorPicker accessibility contract |

---

## 2. Dropdown disclosure

Trigger button lacks `aria-expanded` and `aria-haspopup` in M1 — AT users don't know a panel will open. Gap G-CPKR3.

---

## 3. Color swatch

The swatch `<span style={{ background: current }}>` has no `aria-label` — AT receives no announcement of the current color. The hex string in the button text provides partial context.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPKR3 | High | No `aria-expanded` or `aria-haspopup="dialog"` on trigger — AT users cannot tell a panel will open | Accepted-risk M1 |
| G-CPKR4 | Medium | Color swatch has no accessible name — current color only conveyed via hex text string | Accepted-risk M1 |
