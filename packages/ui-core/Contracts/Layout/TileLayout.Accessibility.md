# TileLayout — Accessibility Contract

- **Component:** TileLayout
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TileLayout.Semantic.md) · [Interaction](./TileLayout.Interaction.md) · [Styling](./TileLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/TileLayout.tsx`
- **Catalog row:** #135 TileLayout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Grid container | No role |
| *(none)* | Tile item | No role |
| *(none)* | Header bar | No role |

No ARIA annotations are present in M1.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TLAYOUT4 | High | No ARIA roles — the grid and tiles are semantically opaque to AT | Accepted-risk M1 |
| G-TLAYOUT5 | High | HTML5 drag-and-drop has no accessible keyboard equivalent for reordering | Accepted-risk M1; see G-TLAYOUT1 |
| G-TLAYOUT6 | Medium | No `aria-label` on tile or header — tile purpose not announced to AT | Accepted-risk M1; tile header text provides visual context only |
