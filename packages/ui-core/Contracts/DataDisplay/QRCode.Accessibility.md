# QRCode — Accessibility Contract

- **Component:** QRCode
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./QRCode.Semantic.md) · [Interaction](./QRCode.Interaction.md) · [Styling](./QRCode.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/QRCode.tsx`
- **Catalog row:** #101 QRCode (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="img"` | `<svg>` | Image role |
| `aria-label` | `<svg>` | `aria-label` prop if provided; otherwise `"QR code: {value}"` |

---

## 2. Accessibility notes

The default `aria-label` embeds the encoded `value` string (e.g., `"QR code: https://app.example.com/invoice/42?token=abc123"`). Screen reader users hear what the QR code encodes, which is useful when `value` is a plain URL or identifier.

**PII risk:** When `value` contains sensitive data — session tokens, payment addresses, personal identifiers — the default label announces that data aloud. In shared environments (open office, accessibility demo, AT with output logging), this is a PII exposure vector.

**Recommended practice:** When `value` is sensitive, supply a purpose-based `aria-label` override:
```tsx
// Default — announces full value (ok for public URLs):
<QRCode value="https://example.com/product/123" />
// aria-label → "QR code: https://example.com/product/123"

// Override — announces purpose only (use when value is sensitive):
<QRCode value={paymentAddress} aria-label="Scan to pay" />
```

No interactive elements — no keyboard or pointer events.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-QR1 | High | Default `aria-label` embeds `value` — announces PII when value is a sensitive token, payment address, or personal identifier | Mitigated by `aria-label` override prop; hosts MUST override when value is sensitive |
