# QRCode — Interaction Contract

- **Component:** QRCode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./QRCode.Semantic.md) · [Accessibility](./QRCode.Accessibility.md) · [Styling](./QRCode.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/QRCode.tsx`
- **Catalog row:** #101 QRCode (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

`QRCode` is a display-only component with no user interaction. It responds only to prop changes.

---

## 2. Reactivity

The QR pattern grid is recomputed via `React.useMemo` when `value` changes. Size and color changes cause a re-render without recomputing the grid.

---

## 3. Known gaps

No interaction gaps — display-only component.
