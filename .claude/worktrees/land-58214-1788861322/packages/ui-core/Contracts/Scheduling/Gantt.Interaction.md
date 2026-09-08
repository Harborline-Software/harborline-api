# Gantt — Interaction Contract

- **Component:** Gantt
- **ADR 0017 family:** Scheduling
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Gantt.Semantic.md) · [Accessibility](./Gantt.Accessibility.md) · [Styling](./Gantt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #65 Gantt (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Gantt baseline)

---

## 1. Task click

Click a task bar or tree row: fires `onTaskClick(task)`. Caller renders a detail popup or navigates.

---

## 2. Drag-to-move

When `editable=true`: drag a task bar horizontally to change its start/end dates. `onTaskUpdate(updatedTask)` fires on drop with new `start`/`end`.

---

## 3. Drag-to-resize

Drag the right edge of a task bar to extend/shrink its duration. Fires `onTaskUpdate(updatedTask)`.

---

## 4. Row expand/collapse

Click the tree expand/collapse icon in the left-pane to show/hide child tasks.

---

## 5. View switch

Changing `view` prop re-renders the timeline header and rescales bar widths.

---

## 6. Scroll

The timeline panes scroll horizontally; the left grid scrolls vertically and stays synchronized with the timeline rows.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-GANTT1 | High | Drag operations are pointer-only — no keyboard equivalent for task move/resize | Accepted-risk M1; task dates editable via edit dialog (caller-provided) as keyboard alternative |
| G-GANTT2 | Medium | Dependency arrow drawing not fully specified (finish-to-start assumed) | Accepted-risk M1; validate relationship types at implementation in M2 |
| G-GANTT3 | Medium | Styling §1 defines a `cursor-col-resize` handle between left and right panes, but no drag-to-resize column-width behavior is specified in this contract — the handle is visual-only in v1 | Accepted-risk M1; column width is fixed at 280px (`w-[280px]`) per Styling §1; resizing deferred to M2 |

---

## Polish expansion (2026-06-11 — Polish-pilot, see _shared/design/polish-gate.md)

Reference: SVAR React Gantt. The following behaviors are REQUIRED additions:

1. **Zoomable multi-row time scale.** `zoom?: 'day' | 'week' | 'month'`
   (default `'day'`). The header renders two stacked scale rows (e.g. month
   over day). Zoom changes column width and scale composition.
2. **Drag to move.** Pointer-drag on a task bar moves it along the timeline,
   snapping to the active scale unit; on release fires
   `onTaskUpdate(task, { start, end })`. Drag never mutates internal copies —
   fully controlled via callback.
3. **Edge resize.** 6px grab zones at bar edges resize start/end with the
   same snapping + `onTaskUpdate` contract. `cursor: ew-resize` affordance.
4. **Progress fill.** `task.progress` (0–100) renders as an inner fill band;
   the % column stays.
5. **Dependency connectors.** Dependencies render as SVG elbow connectors
   with arrowheads from predecessor bar end to successor bar start (replaces
   the footer count).
6. **Task editor.** Double-click a bar (or row) opens an inline editor panel
   (name, start, end, progress) anchored right; Save fires `onTaskUpdate`,
   Escape closes without mutation.
7. **Visual states.** Weekend column shading, row hover wash, refined today
   line. Read-only mode (`readOnly`) disables 2/3/6 but keeps rendering.
