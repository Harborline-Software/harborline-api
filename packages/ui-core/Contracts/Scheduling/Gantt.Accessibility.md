# Gantt — Accessibility Contract

- **Component:** Gantt
- **ADR 0017 family:** Scheduling
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Gantt.Semantic.md) · [Interaction](./Gantt.Interaction.md) · [Styling](./Gantt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #65 Gantt (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Gantt baseline)

---

## 1. Task grid

Left pane: `role="treegrid"`. Row: `role="row"` with `aria-level`, `aria-expanded` (for parent rows). Task title cell: `role="gridcell"`.

---

## 2. Task bars

Each task bar: `role="button"` (not `role="gridcell"` — gridcell is an ARIA hierarchy violation outside a grid container; timeline pane is not a treegrid) in the timeline pane with `aria-label="{title}: {start} to {end}, {percentComplete}% complete"`.

---

## 3. Keyboard navigation

Arrow keys navigate the tree grid rows. `Enter` fires `onTaskClick`. Drag operations (move/resize) have no keyboard equivalent — see G-GANTT1.

---

## 4. Dependency arrows

Dependency SVG arrows: `aria-hidden="true"` (decorative; dependency relationships are described in task `aria-label` via `dependencies` field at implementation time).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-GANTT-A1 | High | Drag-to-move/resize is pointer-only | Accepted-risk M1 (matches G-GANTT1); keyboard edit via dialog is the AT path |
| G-GANTT-A2 | Medium | Complex timeline structure may be overwhelming for screen reader users | Accepted-risk M1; AT users should use the left-pane tree grid as primary navigation |
