# LinearGauge — Styling Contract

- **Component:** LinearGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./LinearGauge.Semantic.md) · [Interaction](./LinearGauge.Interaction.md) · [Accessibility](./LinearGauge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A19 LinearGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik LinearGauge baseline)

---

## 1. Track

`rounded-full bg-muted h-4` (horizontal) or `w-4` (vertical).

---

## 2. Fill bar

`h-full rounded-full bg-primary transition-all duration-400`. Range-band color overrides from `scale.ranges`.

---

## 3. Pointer marker

Thin rectangle at `value` position: `w-0.5 h-6 bg-foreground` overlaid on track.

---

## 4. Scale labels

`text-xs text-muted-foreground` below/beside track at min, max, and any defined `scale.labels` positions.

---

## 5. Value label

`text-sm font-semibold text-foreground` at end of bar.

---

## 6. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`.
