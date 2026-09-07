# Barcode — Styling Contract

- **Component:** Barcode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Barcode.Semantic.md) · [Interaction](./Barcode.Interaction.md) · [Accessibility](./Barcode.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Barcode.tsx`
- **Catalog row:** #11 Barcode (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. SVG element

Inline `width` and `height` from props (defaults 200×80).
`viewBox="0 0 {w} {h}"`
Inline style: `background: {background}` (default `transparent`)

---

## 2. Bar rects (odd-indexed bars)

`<rect x={x} y={0} width={max(1, bw)} height={barHeight} fill={color} />`

`barHeight = height - 16` when label visible, else `height`
`color` default: `currentColor`

---

## 3. Label text (when visible)

`<text x={w/2} y={h-2} textAnchor="middle" fontSize="10" fill={color} fontFamily="monospace">`
Content: `value` string
