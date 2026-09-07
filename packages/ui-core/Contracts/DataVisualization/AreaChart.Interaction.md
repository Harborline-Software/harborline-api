# AreaChart — Interaction Contract

- **Component:** AreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AreaChart.Semantic.md) · [Accessibility](./AreaChart.Accessibility.md) · [Styling](./AreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts AreaChart baseline)
- **Catalog row:** #A1 AreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

When `tooltip=true` (default): hovering the chart area shows a tooltip with the x-axis value and all series values at that x position. Tooltip tracks mouse position horizontally.

### 1.1 Tooltip content model

The default tooltip renders a card-style popover with:

| Slot | Content |
|---|---|
| **Header** | The `categoryKey` value for the hovered x position (e.g., `"Jan 2024"`) |
| **Series rows** | One row per *visible* series: `[color swatch] [series.name]: [formatted value]` |
| **Value format** | Raw number — no unit suffix, no locale formatting in v1 |

Only series that are currently visible (not toggled off via legend) appear in the tooltip rows.

`tooltip=false` suppresses the tooltip entirely. A custom content renderer (`tooltipContent?: ReactNode | ((payload) => ReactNode)`) is **out-of-scope for v1**; host teams that need custom formatting should render their own overlay using the `onHover` callback. This content model is the **canonical reference** for all chart Interaction contracts that expose a `tooltip` prop — sibling charts (LineChart, BarChart, ColumnChart, etc.) inherit this model unless their own contract specifies an override.

---

## 2. Legend interaction

Clicking a legend item toggles that series' visibility (hides/shows the area fill and line).

---

## 3. No click / selection

AreaChart is a read-only visualization. No click handlers on data points.

---

## 4. Zoom / pan

Out of scope for v1. No zoom or pan gestures.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AREA1 | Low | No zoom/pan — large datasets can be hard to read | Accepted-risk M1; deferred to M3; consume a windowed data slice at the call site |
