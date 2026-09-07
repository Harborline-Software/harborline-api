# OrgChart — Accessibility Contract

- **Component:** OrgChart
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./OrgChart.Semantic.md) · [Interaction](./OrgChart.Interaction.md) · [Styling](./OrgChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A24 OrgChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik OrgChart baseline)

---

## 1. Tree structure

Chart root: `role="tree"`. Each node: `role="treeitem"` with `aria-level`, `aria-expanded` (on parent nodes), `aria-label="{title}{subtitle ? ', ' + subtitle : ''}"`. Node children container: `role="group"`.

---

## 2. Toggle button

The expand/collapse chevron: `role="button" aria-label="{Expand|Collapse} {node.title}'s team"`. Part of the treeitem's interactive surface.

---

## 3. Canvas container

Outer pan/zoom canvas: `role="region" aria-label="Organization chart"`. The canvas itself is not keyboard navigable — arrow-key navigation operates on the tree nodes directly.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ORG-A1 | Medium | Large org charts may produce overwhelming tree structure for AT | Accepted-risk M1; collapse state reduces noise; AT users can navigate level-by-level |
| G-ORG-A2 | Medium | Pan/zoom canvas has no keyboard equivalent | Accepted-risk M1; keyboard tree navigation gives full node access without pan |
