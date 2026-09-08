# QRCode — Semantic Contract

- **Component:** QRCode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./QRCode.Interaction.md) · [Accessibility](./QRCode.Accessibility.md) · [Styling](./QRCode.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/QRCode.tsx`
- **Catalog row:** #101 QRCode (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — canvas/SVG QR code renderer

---

## 1. Component purpose

**QRCode** — renders an SVG visual representation of a QR code for a given string value. Uses a deterministic pseudo-QR pattern (with correct finder patterns in three corners) as a visual stand-in. Not a standards-conformant QR encoder — the rendered pattern is not scannable by QR readers.

---

## 2. Props

```typescript
type QRCodeErrorCorrection = 'L' | 'M' | 'Q' | 'H'
type QRCodeEncoding = 'UTF_8' | 'ISO_8859_1'

interface QRCodeProps {
  value: string                          // required; the string to encode
  size?: number | string                 // default: 200 (pixels)
  color?: string                         // default: '#000'
  background?: string                    // default: '#fff'
  errorCorrection?: QRCodeErrorCorrection  // reserved; M1 not used
  encoding?: QRCodeEncoding              // reserved; M1 not used
  border?: number                        // default: 0; quiet zone in pixels
  aria-label?: string                    // overrides the default "QR code: {value}" AT label; use when value is sensitive (see QRCode.Accessibility §2)
  className?: string
}
```

---

## 3. M1 rendering limitation

The QR pattern is generated via a deterministic hash of `value` (linear congruential generator). The three finder patterns (corner squares) are drawn correctly for visual recognition, but the data modules are pseudorandom — not a real QR encoding. The output is **not scannable**.

**Future work:** Replace with a standards-conformant QR library (e.g., `qrcodegen` or `qrcode.js`) before this component can be used in production contexts where scanning is required.

---

## 4. Module count

Fixed at 25×25 modules in M1 regardless of data length or error correction level.
