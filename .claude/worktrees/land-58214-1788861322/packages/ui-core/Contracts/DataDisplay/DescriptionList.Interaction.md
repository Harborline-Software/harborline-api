# DescriptionList — Interaction Contract

- **Component:** DescriptionList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DescriptionList.Semantic.md) · [Accessibility](./DescriptionList.Accessibility.md) · [Styling](./DescriptionList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DescriptionList.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

DescriptionList is a static display component. It has no interactive states, no event handlers, and no keyboard behavior of its own.

All interactivity within term/description content is caller-controlled via `React.ReactNode` slots.

---

## 2. Known gaps

None identified.
