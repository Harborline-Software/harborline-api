# CurrencyAmount — Semantic Contract

- **Component:** CurrencyAmount
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CurrencyAmount.Interaction.md) · [Accessibility](./CurrencyAmount.Accessibility.md) · [Styling](./CurrencyAmount.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/CurrencyAmount.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<span>` formatted value display

---

## 1. Purpose

CurrencyAmount is the canonical primitive for rendering a monetary value as a locale-aware, currency-formatted string inside a `<span>`. It wraps `Intl.NumberFormat` with the `style: 'currency'` option, producing output like `$1,234.56` or `€ 1.234,56`.

CurrencyAmount is **presentational and stateless**. It performs no arithmetic, applies no sign convention, and makes no assumptions about positive/negative display beyond what `Intl.NumberFormat` provides.

Primary usage positions:

- **DataGrid cells** — amount columns in invoice, payment, and ledger tables.
- **Summary cards** — total amounts in dashboard KPI tiles.
- **Form review panels** — rendered invoice line items before submission.

---

## 2. Data model

```typescript
interface CurrencyAmountProps extends Omit<HTMLAttributes<HTMLSpanElement>, 'children'> {
  amount: number
  currency?: string
  locale?: string
}
```

CurrencyAmount intentionally omits `children` from `HTMLAttributes` — the formatted string IS the content; no children are accepted.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `amount` | `number` | _required_ | The monetary value to format. Accepts any finite number; `NaN` and `Infinity` produce browser-defined output via `Intl.NumberFormat`. |
| `currency` | `string` | `'USD'` | ISO 4217 currency code. Passed directly to `Intl.NumberFormat`. Invalid codes throw in some environments. |
| `locale` | `string` | `'en-US'` | BCP 47 locale tag. Controls digit grouping, decimal separator, and currency symbol position. |
| `...rest` | `HTMLAttributes<HTMLSpanElement>` (minus `children`) | — | All other valid `<span>` attributes are spread onto the root element: `className`, `id`, `data-*`, `aria-*`, `title`, etc. |

### 3.1 Formatting semantics

The formatted string is produced by:

```typescript
new Intl.NumberFormat(locale, { style: 'currency', currency }).format(amount)
```

This means:

- Currency symbol placement follows locale conventions (e.g., `$` prefix for `en-US`; `€` suffix for `de-DE`).
- Decimal precision follows the currency's standard (2 decimals for USD/EUR; 0 for JPY).
- Negative values render with the locale's negative convention (e.g., `−$1,234.56` or `($1,234.56)` depending on locale).

### 3.2 HTML attribute passthrough

CurrencyAmount spreads all remaining `HTMLAttributes<HTMLSpanElement>` onto the root `<span>`. This enables hosts to apply `className`, `title`, `aria-label`, etc. without needing a wrapper element.

---

## 4. Events

CurrencyAmount has **no events**. It is presentational.

---

## 5. Slots

CurrencyAmount has no slot props and accepts no `children`. The formatted value is the entire content.

---

## 6. Component composition

- **DataGrid cell:** `<CurrencyAmount amount={invoice.total} />` as cell content in an amount column.
- **Summary card:** `<CurrencyAmount amount={totalRevenue} className="text-2xl font-bold" />` for KPI display.
- **Table total row:** `<CurrencyAmount amount={sum} locale="en-GB" currency="GBP" />` for multi-currency tenants.

---

## 7. Deferred features

- **Sign display control** — explicit `+` prefix for positive amounts (e.g., `Intl.NumberFormat` `signDisplay` option). Deferred; host can format the string externally for special sign needs.
- **Compact notation** — `$1.2M` short form. Deferred; use `notation: 'compact'` externally.
- **Null/undefined guard** — currently no guard for `amount` being `undefined` or `null` at runtime (TypeScript types `number` but JS may supply `undefined`). Deferred; host must guard.
- **Accounting notation** — `(1,234.56)` for negative values. Deferred; `Intl.NumberFormat` `currencySign: 'accounting'` can be added as a prop.
