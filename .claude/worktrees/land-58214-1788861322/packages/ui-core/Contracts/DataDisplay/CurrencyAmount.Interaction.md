# CurrencyAmount — Interaction Contract

- **Component:** CurrencyAmount
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyAmount.Semantic.md) · [Interaction](./CurrencyAmount.Interaction.md) · [Accessibility](./CurrencyAmount.Accessibility.md) · [Styling](./CurrencyAmount.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/CurrencyAmount.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

CurrencyAmount is **presentational and stateless**. There is no interaction surface.

---

## 2. No interaction surface

CurrencyAmount does **not**:

- Fire any event callbacks.
- Maintain internal state.
- Respond to keyboard input.
- Participate in tab order.

The root `<span>` is not focusable by default. Hosts can add `tabIndex`, `onClick`, etc. via HTML attribute passthrough — but these are not part of CurrencyAmount's contract.

---

## 3. Prop update behaviour

When `amount`, `currency`, or `locale` props change, the component re-renders synchronously with the new formatted value. There is no animation, no debounce, and no loading state.

---

## 4. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No guard for `NaN` / `Infinity` `amount` values | `Intl.NumberFormat("en-US",{style:"currency",currency:"USD"}).format(NaN)` returns `"$NaN"`. Host must guard. |
| I2 | No copy-to-clipboard built in | Host composes CurrencyAmount inside an interactive element if copy is needed. |
