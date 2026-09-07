# Toolbar — Accessibility Contract

- **Component:** Toolbar
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Toolbar.Semantic.md) · [Interaction](./Toolbar.Interaction.md) · [Styling](./Toolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Toolbar.tsx`
- **Catalog row:** #139 Toolbar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. ARIA structure

Toolbar renders a plain `<div>` — no ARIA roles or attributes are applied by the component itself. The Toolbar passes through all `React.HTMLAttributes<HTMLDivElement>` props, so callers may add `role="toolbar"` or `aria-label` if the WAI-ARIA toolbar pattern is needed.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TLBR1 | Low | No `role="toolbar"` by default — callers must add it explicitly if AT toolbar semantics are desired | Accepted-risk M1 |
| G-TLBR2 | Low | No built-in keyboard roving-tabIndex management (WAI-ARIA toolbar pattern) | Accepted-risk M1 |
