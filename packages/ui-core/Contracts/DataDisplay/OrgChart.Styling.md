# OrgChart — Styling Contract

- **Component:** OrgChart
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./OrgChart.Semantic.md) · [Interaction](./OrgChart.Interaction.md) · [Accessibility](./OrgChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A24 OrgChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik OrgChart baseline)

---

## 1. Canvas

`overflow-hidden bg-muted/20 border border-border rounded-md` with explicit `height` (default 480px). Inner transform container handles pan/zoom via CSS `transform: scale(Z) translate(X, Y)`.

---

## 2. Nodes

`bg-card border border-border rounded-md shadow-sm w-[180px] p-3 flex flex-col items-center gap-1`. Hover: `shadow-md border-primary/40`. Focused: `ring-2 ring-primary`.

---

## 3. Node content

Avatar: `w-10 h-10 rounded-full object-cover`. Title: `text-sm font-medium text-foreground text-center`. Subtitle: `text-xs text-muted-foreground text-center`.

---

## 4. Connector lines

SVG paths between nodes: `stroke: hsl(var(--border))` `stroke-width: 1.5` `fill: none`. Elbow-style connectors (vertical then horizontal).

---

## 5. Toggle chevron

`h-4 w-4 text-muted-foreground` positioned below the node. Rotates 180° when expanded via `transition-transform duration-200`.

---

## 6. Design tokens

Uses: `hsl(var(--card))`, `hsl(var(--border))`, `hsl(var(--muted))`, `hsl(var(--primary))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`.
