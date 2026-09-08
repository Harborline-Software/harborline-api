# Splitter — Accessibility Contract

- **Component:** Splitter
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Splitter.Semantic.md) · [Interaction](./Splitter.Interaction.md) · [Styling](./Splitter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Splitter.tsx`
- **Catalog row:** #125 Splitter (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="separator"` | Divider `<div>` | ARIA separator (resizable) |
| `aria-orientation={orientation}` | Divider | `'horizontal'` or `'vertical'` |
| `aria-valuenow={currentPercent}` | Divider | Current pane size as percentage 0–100 (deferred; see G-SP4) |
| `aria-valuemin={0}` | Divider | Minimum resize value |
| `aria-valuemax={100}` | Divider | Maximum resize value |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SP4 | Critical | Divider has `role="separator"` but WAI-ARIA requires `aria-valuenow`, `aria-valuemin`, `aria-valuemax` for a resizable separator | Accepted-risk M1; structural separator pattern; resize ARIA values deferred |
| G-SP5 | Critical | No keyboard resize: WAI-ARIA splitter pattern requires ArrowLeft/ArrowRight (horizontal) or ArrowUp/ArrowDown (vertical) to move the divider | Accepted-risk M1; see G-SP1 |
| G-SP6 | Medium | No `aria-label` or `aria-labelledby` on divider to describe which panes it separates | Accepted-risk M1 |
