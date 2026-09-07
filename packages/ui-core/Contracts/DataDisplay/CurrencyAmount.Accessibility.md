# CurrencyAmount — Accessibility Contract

- **Component:** CurrencyAmount
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyAmount.Semantic.md) · [Interaction](./CurrencyAmount.Interaction.md) · [Accessibility](./CurrencyAmount.Accessibility.md) · [Styling](./CurrencyAmount.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/CurrencyAmount.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CurrencyAmount renders a `<span>` containing locale-formatted currency text. Its accessibility surface is narrow: screen readers read the formatted string as inline text. The contract pins the expectations for how AT processes the output and identifies gaps.

---

## 2. Root element + role

| Attribute | Value |
|---|---|
| Element | `<span>` |
| Implicit ARIA role | `generic` (inline text) |
| `aria-label` | Not set by default; host may add via `...rest` passthrough |

The formatted text is the accessible name for surrounding context (e.g., the table cell or card that contains it). CurrencyAmount itself carries no ARIA role.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — `<span>` with text content is correctly read as inline text.

---

## 3. Screen reader behaviour

Screen readers read `Intl.NumberFormat` output as a text string.

| Input | `en-US` output | SR reads (NVDA/JAWS) |
|---|---|---|
| `amount={1234.56}` | `$1,234.56` | "dollar sign one comma two three four point five six" (varies by SR/voice) |
| `amount={-500}` | `-$500.00` | "negative dollar sign five hundred" |
| `amount={0}` | `$0.00` | "dollar sign zero point zero zero" |

**SR verbosity concern:** Some screen readers read currency symbols and punctuation verbosely. This is a known limitation of raw `Intl.NumberFormat` output in the absence of a human-readable `aria-label`.

**Recommendation for important financial figures:** Hosts rendering large financial totals in summary contexts SHOULD add an `aria-label` with a prose description:

```tsx
<CurrencyAmount
  amount={totalRevenue}
  aria-label={`Total revenue: ${formattedTotalRevenue}`}
/>
```

This is optional for table cell usage where the column header provides sufficient context.

---

## 4. Keyboard

CurrencyAmount is not focusable. No keyboard contract applies.

---

## 5. Known gaps

| # | Item | Resolution path |
|---|---|---|
| A1 | SR verbosity on currency symbols (e.g. "$" read as "dollar sign") | Host adds `aria-label` with prose text for prominent financial values |
| A2 | Negative amounts may be read inconsistently across SR/locale combinations | No automated mitigation; document as known limitation |
| A3 | No semantic `role="img"` or structured data for machine-readable amounts | Out of scope for this component; use `data-*` attributes at host level |
