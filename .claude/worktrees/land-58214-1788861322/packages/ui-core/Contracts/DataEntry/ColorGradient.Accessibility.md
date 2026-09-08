# ColorGradient — Accessibility Contract

- **Component:** ColorGradient
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorGradient.Semantic.md) · [Interaction](./ColorGradient.Interaction.md) · [Styling](./ColorGradient.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorGradient.tsx`
- **Catalog row:** #29 ColorGradient (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Gradient canvas `<div>` | No ARIA role or label — Gap G-CG3 |
| `aria-label="Hue"` | Hue `<input type="range">` | Labels hue slider |
| `aria-label="Opacity"` | Opacity `<input type="range">` | Labels opacity slider |
| `aria-label="Hex color"` | Hex `<input type="text">` | Labels hex text input |

---

## 2. Keyboard access to gradient

The gradient canvas is only clickable — no keyboard mechanism to change saturation or brightness in M1. Keyboard users rely on the hex text input to set exact colors.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CG3 | High | Gradient canvas has no role, no aria-label, and no keyboard interface — not accessible | Accepted-risk M1; hex input provides fallback for keyboard/AT users |
