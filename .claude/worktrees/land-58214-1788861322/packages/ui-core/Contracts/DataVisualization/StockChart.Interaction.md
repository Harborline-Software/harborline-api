# StockChart — Interaction Contract

- **Component:** StockChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./StockChart.Semantic.md) · [Accessibility](./StockChart.Accessibility.md) · [Styling](./StockChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 StockChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik StockChart baseline)

---

## 1. Hover crosshair

Vertical crosshair + tooltip showing OHLC values (and volume if enabled) for the hovered date.

---

## 2. Navigator drag

When `navigator=true`: the selection handles in the sub-chart are draggable, adjusting the main chart's x-axis range.

---

## 3. Navigator click-to-seek

Clicking inside the navigator (not on handles) shifts the selection window to that position.

---

## 4. Keyboard

Navigator selection window: Left/Right arrow keys shift the window by one period. Escape resets to full range.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-STOCK1 | Medium | Navigator drag interactions are pointer-only (keyboard coverage is limited) | Accepted-risk M1 |
