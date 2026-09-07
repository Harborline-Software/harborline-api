# ColorPalette — Interaction Contract

- **Component:** ColorPalette
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPalette.Semantic.md) · [Accessibility](./ColorPalette.Accessibility.md) · [Styling](./ColorPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPalette.tsx`
- **Catalog row:** #30 ColorPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Click

`onClick` on swatch: calls `select(color)` → `onValueChange(color)` if not disabled.

---

## 2. Keyboard

| Key | Behaviour |
|---|---|
| `Tab` | Moves focus between swatches |
| `Enter` / `Space` | Selects focused swatch (native button behavior) |

No arrow-key navigation between swatches in M1.

---

## 3. Disabled

Global `disabled`: all swatches receive `opacity-50 pointer-events-none` via container. Individual swatch `disabled` is not supported.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPL1 | Low | No arrow-key roving navigation between swatches — radiogroup pattern expects arrow keys | Accepted-risk M1; Tab navigation works |
