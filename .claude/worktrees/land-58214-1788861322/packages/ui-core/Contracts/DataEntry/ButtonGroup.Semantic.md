# ButtonGroup — Semantic Contract (Alias)

- **Component:** ButtonGroup
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [ToggleGroup](./ToggleGroup.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/buttons/ButtonGroup.tsx`
- **Catalog row:** #18 ButtonGroup (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` wrapping buttons

---

ButtonGroup is an alias for **ToggleGroup** used in contexts where a visually connected strip of action buttons is needed. When button selection state is not required, use `type="multiple"` with an empty `value` array and ignore `onValueChange`.

For stateless connected button rows (no selection), ToggleGroup with `variant="outline"` and no pressed state provides the visual grouping. For selection-aware button groups, use ToggleGroup normally.

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the ToggleGroup
contract family. The ui-react alias has a separate wrapper implementation so it can support
children-based composition while preserving the canonical group's semantics.

## Accessible-name passthrough

The wrapper accepts both standard group-naming mechanisms and passes the selected one to its
container:

```typescript
interface ButtonGroupProps {
  'aria-label'?: string
  'aria-labelledby'?: string
}
```

Hosts must provide one of these props whenever `selection` gives the container `role="group"`.
`aria-label` supplies a direct name; `aria-labelledby` references visible labeling content. The
props are additive and do not change selection, sizing, or child-composition behavior.
