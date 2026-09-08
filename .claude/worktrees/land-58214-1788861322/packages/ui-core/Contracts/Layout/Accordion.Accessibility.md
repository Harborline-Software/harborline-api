# Accordion — Accessibility Contract

- **Component:** Accordion
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Accordion.Semantic.md) · [Interaction](./Accordion.Interaction.md) · [Styling](./Accordion.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Accordion.tsx`
- **Catalog rows:** #155 Accordion / #35 PanelBar (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<h3>` | Header wrapper | Heading level 3 (landmark heading structure) |
| `type="button"` | Header `<button>` | Explicit button type |
| `id={headerId}` | Header `<button>` | `{value}-header` — referenced by panel |
| `aria-expanded={isOpen}` | Header `<button>` | Open/closed state |
| `aria-controls={panelId}` | Header `<button>` | `{value}-panel` — points to controlled panel |
| `disabled` | Header `<button>` | HTML disabled; disabled items skip keyboard nav |
| `id={panelId}` | Panel `<div>` | `{value}-panel` |
| `role="region"` | Panel `<div>` | Landmark region |
| `aria-labelledby={headerId}` | Panel `<div>` | Labeled by the header button |
| `hidden={!isOpen}` | Panel `<div>` | Hides panel from AT and layout when closed |
| `aria-hidden="true"` | ChevronDown icon | Decorative |

---

## 2. Focus management

Focus stays on the header button throughout toggle interactions. Keyboard navigation moves focus between headers via ArrowUp/ArrowDown/Home/End (managed via `headerRefs.current[i]?.focus()`).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ACCORD1 | Low | `<h3>` heading level is hardcoded — callers cannot change the heading level to match document outline | Accepted-risk M1 |
