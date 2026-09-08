# FloatingLabel — Accessibility Contract

- **Component:** FloatingLabel
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingLabel.Semantic.md) · [Interaction](./FloatingLabel.Interaction.md) · [Styling](./FloatingLabel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FloatingLabel.tsx`
- **Catalog row:** #61 FloatingLabel (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `htmlFor={id ?? child.props.id}` | `<label>` | Associates label with the input |

No additional ARIA on the wrapper `<div>`. The `<label>` is always rendered (visible, not `aria-label`). AT reads the label text as the input's accessible name.

---

## 2. Label position and AT

The `<label>` is visible in all states (floating up or in placeholder position). AT reads the label text regardless of CSS position. The animated CSS transformation does not affect AT.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FL3 | High | If the child has no `id` and no `id` prop is passed to FloatingLabel, `htmlFor` is `undefined` — the label is not programmatically associated with the input | Blocking-before-v1-ship — WCAG SC 1.3.1 (Level A) violation; must resolve before v1 ship |
| G-FL4 | Low | Injected `placeholder=" "` (space) may be read by some AT as a valid placeholder announcement | Accepted-risk M1 |
