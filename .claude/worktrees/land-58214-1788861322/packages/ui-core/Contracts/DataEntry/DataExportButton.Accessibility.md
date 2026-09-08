# DataExportButton — Accessibility Contract

- **Component:** DataExportButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataExportButton.Semantic.md) · [Interaction](./DataExportButton.Interaction.md) · [Styling](./DataExportButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DataExportButton.tsx`
- **Catalog row:** #A-DEX DataExportButton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Single-format mode

Standard `<button>` with visible `{label}` text — accessible name derived from button text content.

---

## 2. Multi-format mode

Main button: `aria-haspopup="menu"`, `aria-expanded={open}`.

Dropdown: `role="menu"`.

Each format button: `role="menuitem"`.

Format icons: `aria-hidden` emoji spans.

---

## 3. Loading state

Spinner: `aria-hidden="true"`. No `aria-busy` or `aria-label` change during loading — screen readers receive no loading announcement (G-DEX-A1).

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DEX-A1 | Medium | No `aria-busy` or live region during export — screen reader users receive no feedback that export is in progress | Accepted-risk M1 |
| G-DEX-A2 | High | No keyboard navigation or focus management inside `role="menu"` — same gap as ActionMenu | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
