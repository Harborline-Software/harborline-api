# FieldArray — Styling Contract

- **Component:** FieldArray
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldArray.Semantic.md) · [Interaction](./FieldArray.Interaction.md) · [Accessibility](./FieldArray.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldArray.tsx`
- **Catalog row:** #54 FieldArray (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Component styling

`FieldArray` renders no DOM and applies no styles. It is a headless render-prop utility.

All layout, spacing, and visual styling of the rendered fields is the caller's responsibility via the `children` render function.
