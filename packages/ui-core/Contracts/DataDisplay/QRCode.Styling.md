# QRCode — Styling Contract

- **Component:** QRCode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./QRCode.Semantic.md) · [Interaction](./QRCode.Interaction.md) · [Accessibility](./QRCode.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/QRCode.tsx`
- **Catalog row:** #101 QRCode (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. SVG element

Inline `width` and `height` from `size` prop (default 200).
`viewBox="0 0 {px} {px}"`
Inline style: `background: {background}` (default `#fff`)

---

## 2. Background rect (when border > 0)

`<rect x={0} y={0} width={px} height={px} fill={background} />`

---

## 3. Module rects

Each "on" module: `<rect x={border + c * cellSize} y={border + r * cellSize} width={cellSize} height={cellSize} fill={color} />`

`cellSize = (px - border * 2) / 25` (25 modules fixed)

---

## 4. Color

Module fill: `color` prop (default `#000`).
Background: `background` prop (default `#fff`) applied as inline SVG `style.background`.
