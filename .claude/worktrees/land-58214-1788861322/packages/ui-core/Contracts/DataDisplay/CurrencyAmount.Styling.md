# CurrencyAmount — Styling Contract

- **Component:** CurrencyAmount
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyAmount.Semantic.md) · [Interaction](./CurrencyAmount.Interaction.md) · [Accessibility](./CurrencyAmount.Accessibility.md) · [Styling](./CurrencyAmount.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/CurrencyAmount.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CurrencyAmount renders a bare `<span>` with no default visual styles applied by the component. All styling is entirely host-driven via the `className` prop passthrough.

---

## 2. Base recipe

```
(no classes applied by default)
```

The component is `<span {...rest}>{formatted}</span>` — the only meaningful default is the formatted text content. Hosts apply all visual styles.

---

## 3. Recommended host patterns

CurrencyAmount is intended to be used in dense data contexts. Typical class patterns:

| Context | Recommended classes |
|---|---|
| DataGrid amount cell | `tabular-nums text-right` |
| Dashboard KPI | `text-2xl font-bold text-foreground` |
| Negative amounts (host highlights) | `text-red-600` or `text-destructive` |
| Positive amounts (host highlights) | `text-green-600` or `text-success` |
| Muted secondary amounts | `text-muted-foreground text-sm` |

**Important:** CurrencyAmount itself never applies negative/positive colouring — that semantic decision belongs to the host.

---

## 4. Token surface

CurrencyAmount has no `--sf-*` tokens. Token-based theming is applied by the host via the `className` / CSS cascade.

---

## 5. Visual states

CurrencyAmount has one visual state: its rendered text. No hover, focus, active, or disabled states.

---

## 6. Responsive behaviour

No responsive behaviour. Font size, alignment, and spacing are entirely host-controlled.

---

## 7. Tabular numbers

For financial tables, hosts MUST apply `tabular-nums` (Tailwind: `tabular-nums`) to ensure digit alignment in columns. This is not applied by default because CurrencyAmount is used in both table and prose contexts.
