# Gantt — Semantic Contract

- **Component:** Gantt
- **ADR 0017 family:** Scheduling
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./Gantt.Interaction.md) · [Accessibility](./Gantt.Accessibility.md) · [Styling](./Gantt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Gantt / DHTMLX Gantt baseline)
- **Catalog row:** #65 Gantt (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Gantt baseline)

---

## 1. Component purpose

**Gantt** — a project-management chart combining a task tree grid on the left with a horizontal timeline of task bars on the right. Supports task dependencies (arrows between bars), milestones, and resource assignments. Used for project planning and maintenance scheduling.

---

## 2. Props (planned)

```typescript
interface GanttTask {
  id: string | number
  title: string
  start: Date
  end: Date
  parentId?: string | number        // for tree hierarchy
  percentComplete?: number          // 0–100; renders fill in bar
  milestone?: boolean               // point-in-time; renders diamond
  dependencies?: string[]           // array of task IDs this task depends on
  color?: string
  [key: string]: unknown
}

interface GanttColumn {
  field: keyof GanttTask | string
  title: string
  width?: number
}

interface GanttProps {
  tasks: GanttTask[]
  columns?: GanttColumn[]           // left-pane columns; default: [{field:'title',title:'Task'}]
  view?: 'day' | 'week' | 'month' | 'quarter' | 'year'  // default: 'week'
  timelineStart?: Date              // default: min task start - 2 weeks
  timelineEnd?: Date                // default: max task end + 2 weeks
  editable?: boolean                // allow drag/resize; default: false
  onTaskUpdate?: (task: GanttTask) => void
  onTaskClick?: (task: GanttTask) => void
  showDependencies?: boolean        // draw dependency arrows; default: true
  showProgress?: boolean            // show percent-complete fill; default: true
  rowHeight?: number                // default: 36
  className?: string
}
```

---

## 3. Task tree

`parentId` links create a collapsible tree in the left-pane grid. Collapsed parent hides child task rows.

---

## 4. Timeline header

Two-row header: upper row shows larger time unit (month for week view); lower row shows individual periods (day headers for week view).

---

## 5. Dependencies

When `showDependencies=true`: finish-to-start dependency arrows are drawn between task bars as SVG polylines.

---

## 6. Dependency model edge cases

- **Circular dependencies** — `tasks[A].dependencies = ['B']` and `tasks[B].dependencies = ['A']` creates a cycle. The Gantt component does NOT validate or resolve cycles; rendering proceeds with the first arrow drawn per dependency declaration. Consumers are responsible for detecting cycles before passing data.
- **Unknown IDs** — a dependency referencing a task ID not present in `tasks` is silently ignored (no arrow drawn; no error thrown).
- **Only finish-to-start** is supported in v1. Start-to-start, finish-to-finish, and start-to-finish relationship types are not modeled; the `dependencies` array is a list of finish-to-start predecessor IDs only.
