# Gantt — Styling Contract

- **Component:** Gantt
- **ADR 0017 family:** Scheduling
- **Contract type:** Styling
- **Polish reference:** https://svar.dev/react/gantt/ (pinned 2026-06-11, Polish-pilot — see _shared/design/polish-gate.md)
- **Polish reference (primary, KendoReact):** https://www.telerik.com/kendo-react-ui/components/gantt
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Gantt.Semantic.md) · [Interaction](./Gantt.Interaction.md) · [Accessibility](./Gantt.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #65 Gantt (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Gantt baseline)

---

## 1. Split-pane layout

Root: `flex w-full overflow-hidden border border-border rounded-md`. Left pane (task grid): `flex-shrink-0 w-[280px] overflow-y-auto`. Resize handle: `w-1 cursor-col-resize bg-border hover:bg-primary/40 transition-colors`. Right pane (timeline): `flex-1 overflow-x-auto overflow-y-auto`.

---

## 2. Timeline header

Header row: `sticky top-0 z-10 bg-background border-b border-border text-xs text-muted-foreground`. Unit cells sized to match column width in the selected `view`.

See G-GANTT-STY1 for the sticky-header layout constraint.

---

## 9. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-GANTT-STY1 | High | `position: sticky` does not work when the scroll ancestor has both `overflow-x` and `overflow-y` set. The timeline pane (§1) sets `overflow-x-auto overflow-y-auto` — this breaks sticky. Fix-in-M1: separate the horizontal scroll container from the vertical scroll container; render a fixed-height header outside the scroll container and synchronize its `scrollLeft` via a `useEffect` binding. | Fix-in-M1 |

---

## 3. Task rows

Row height controlled by `rowHeight` prop (default 36px). Alternating rows: `bg-background` / `bg-muted/30`. Row hover: `bg-accent/20`.

---

## 4. Task bars

Bar container: `absolute top-[6px] rounded-sm h-[24px] min-w-[4px]`. Fill: `bg-primary`. Custom color: `style={{ backgroundColor: task.color }}`. Milestone diamond: `rotate-45 w-[16px] h-[16px] bg-primary` centered on milestone date. Text inside bar (if space): `text-xs text-primary-foreground truncate px-1`.

---

## 5. Progress fill

Inner div absolutely positioned: `bg-primary/60 h-full rounded-sm` with `width: {percentComplete}%`.

---

## 6. Today line

Vertical rule: `absolute top-0 bottom-0 w-[2px] bg-destructive/70 pointer-events-none z-10`.

---

## 7. Dependency arrows

SVG overlay on the timeline pane: `absolute inset-0 pointer-events-none`. Arrows: `stroke: hsl(var(--muted-foreground))` `stroke-width: 1.5` `fill: none`. Arrowhead: small filled triangle `fill: hsl(var(--muted-foreground))`.

---

## 8. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--primary-foreground))`, `hsl(var(--muted))`, `hsl(var(--muted-foreground))`, `hsl(var(--border))`, `hsl(var(--background))`, `hsl(var(--accent))`, `hsl(var(--destructive))`.
