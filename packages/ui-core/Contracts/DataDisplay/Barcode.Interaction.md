# Barcode — Interaction Contract

- **Component:** Barcode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Barcode.Semantic.md) · [Accessibility](./Barcode.Accessibility.md) · [Styling](./Barcode.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Barcode.tsx`
- **Catalog row:** #11 Barcode (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

`Barcode` is a display-only component with no user interaction.

---

## 2. Reactivity

Bar pattern is recomputed via `React.useMemo` when `value`, `type`, or `width` changes.

---

## 3. Known gaps

No interaction gaps — display-only component.
