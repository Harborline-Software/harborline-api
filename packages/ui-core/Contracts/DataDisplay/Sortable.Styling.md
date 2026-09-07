# Sortable — Styling Contract

- **Component:** Sortable
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sortable.Semantic.md) · [Interaction](./Sortable.Interaction.md) · [Accessibility](./Sortable.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Sortable.tsx`
- **Catalog row:** #121 Sortable (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex flex-col`

---

## 2. Item

Base: `transition-transform`
Drag-over state (over, not source): `ring-2 ring-primary rounded-md`

No intrinsic padding or spacing — item styling is delegated to `itemRender`.
