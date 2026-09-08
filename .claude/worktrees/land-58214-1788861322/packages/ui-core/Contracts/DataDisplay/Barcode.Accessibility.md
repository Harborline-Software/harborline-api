# Barcode — Accessibility Contract

- **Component:** Barcode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Barcode.Semantic.md) · [Interaction](./Barcode.Interaction.md) · [Styling](./Barcode.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Barcode.tsx`
- **Catalog row:** #11 Barcode (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="img"` | `<svg>` | Image role |
| `aria-label` | `<svg>` | `aria-label` prop if provided; otherwise `"Barcode: {value}"` |

---

## 2. Accessibility notes

The default `aria-label` embeds the encoded `value` string. Screen reader users hear what the barcode encodes, which is useful for product SKUs or tracking numbers.

**PII risk:** When `value` contains a personal identifier, medical record number, or other sensitive data, the default label announces that data aloud. Supply a purpose-based `aria-label` override when value is sensitive:
```tsx
// Default — announces value (ok for public product SKUs):
<Barcode value="SKU-12345" />
// aria-label → "Barcode: SKU-12345"

// Override — announces purpose only (use when value is sensitive):
<Barcode value={patientId} aria-label="Patient identifier barcode" />
```

The text label (when `label=true`) is inside the SVG and is part of the SVG image read via `aria-label` — it is not separately announced by AT.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BC1 | High | Default `aria-label` embeds `value` — announces PII when value is a personal identifier, medical ID, or other sensitive data | Mitigated by `aria-label` override prop; hosts MUST override when value is sensitive |
