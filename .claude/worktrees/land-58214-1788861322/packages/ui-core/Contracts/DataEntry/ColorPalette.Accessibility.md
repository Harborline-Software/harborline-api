# ColorPalette — Accessibility Contract

- **Component:** ColorPalette
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPalette.Semantic.md) · [Interaction](./ColorPalette.Interaction.md) · [Styling](./ColorPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPalette.tsx`
- **Catalog row:** #30 ColorPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="radiogroup"` | Container `<div>` | Groups swatches as mutually exclusive |
| `aria-label="Color palette"` | Container `<div>` | Names the group |
| `<button type="button">` | Each swatch | Native button |
| `role="radio"` | Each swatch | Identifies as radio button |
| `aria-checked={color === current}` | Each swatch | `true` for selected; `false` for others |
| `aria-label={color}` | Each swatch | Hex string as accessible name |

---

## 2. AT announcement

AT reads: `"Color palette, radiogroup"` on group focus, then `"#ff0000, radio, 1 of 24"` per swatch. Hex labels are the only identification — not human-readable color names (Gap G-CPL2).

---

## 3. Selection ring

Selected swatch: `ring-2 ring-primary ring-offset-1`. Visible to sighted users; AT uses `aria-checked`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPL2 | Low | Swatch `aria-label` is a hex string — not a human-readable color name (e.g. `"Red"`) | Accepted-risk M1; hex is machine-readable; color names would require a lookup table |
