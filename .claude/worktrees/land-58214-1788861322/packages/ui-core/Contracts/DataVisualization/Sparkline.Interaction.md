# Sparkline — Interaction Contract

- **Component:** Sparkline
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sparkline.Semantic.md) · [Accessibility](./Sparkline.Accessibility.md) · [Styling](./Sparkline.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Sparkline baseline)
- **Catalog row:** #120 Sparkline (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Sparkline baseline)

---

## 1. Default state

Renders static SVG. No interactive behavior unless `tooltip=true` or `markers=true`.

---

## 2. Hover tooltip

When `tooltip=true`: mousemove over the SVG surface identifies the nearest data point (by x-position) and renders a floating tooltip with the value. mouseleave hides the tooltip.

---

## 3. Marker highlight

When `markers=true`: data-point dots are always visible. On hover, the nearest dot increases in size (visual feedback only, no event).

---

## 4. Resize

Sparkline reacts to container resize via ResizeObserver (when width is `'100%'`). Redraws SVG on container width change.

---

## 5. No click / selection

Sparklines are read-only data displays. No click events, no selection, no zoom. If interactivity is required, use a full Chart component.

---

## 6. Known gaps

None identified for forward-spec. Validate tooltip positioning against actual implementation in M2.
