# Scheduler — Styling Contract

- **Component:** Scheduler
- **ADR 0017 family:** Scheduling
- **Contract type:** Styling
- **Polish reference:** https://svar.dev/react/calendar/ (pinned 2026-06-11, Polish-pilot — see _shared/design/polish-gate.md)
- **Polish reference (primary, KendoReact):** https://www.telerik.com/kendo-react-ui/components/scheduler
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Scheduler.Semantic.md) · [Interaction](./Scheduler.Interaction.md) · [Accessibility](./Scheduler.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #114 Scheduler (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Scheduler baseline)

---

## 1. Root container

`flex flex-col h-full border border-border rounded-md overflow-hidden bg-background`

---

## 2. Toolbar

`flex items-center justify-between px-4 py-2 border-b border-border bg-muted/30`

Navigation buttons: `Button variant="ghost" size="icon"` (arrow icons)

Current period label: `text-sm font-semibold text-foreground`

View switcher: tab-style toggle strip

---

## 3. Time grid (day/week view)

Header row: time labels + day column headers. `bg-muted/50 text-xs text-muted-foreground`

Time slot rows: `h-8 border-b border-border/50`

Current time indicator: `absolute left-0 right-0 border-t-2 border-primary z-10`

---

## 4. Event chips

`absolute rounded-sm text-xs px-1 py-0.5 overflow-hidden cursor-pointer`

Background: event `color` prop or default `bg-primary`

Text: `text-primary-foreground`

---

## 5. Month grid

Day cells: `min-h-[80px] border-r border-b border-border/50 p-1`

Today highlight: `bg-accent/30`

---

## 6. Design tokens

Uses design tokens: `border-border`, `bg-background`, `bg-muted`, `text-foreground`, `text-muted-foreground`, `bg-primary`, `text-primary-foreground`, `border-primary`, `bg-accent`.

---

## Recurrence styling — 2026-06-12

> **Wave tag:** W-SCHED-R1

### R-1. Recurrence indicator token

A new design token carries the recurrence indicator color. This follows the existing `border-primary`
/ `bg-accent` pattern — it is a semantic token, not a hard-coded color.

```css
/* New tokens (add to design-token registry) */
--scheduler-recurrence-indicator:   hsl(var(--primary))
--scheduler-recurrence-exception:   hsl(var(--muted-foreground))
```

These tokens are set to existing palette values by default so the indicator blends with the
component's existing visual language. Themes can override them independently.

### R-2. Recurrence indicator on event chips (W-SCHED-R1)

**Series occurrence chips** (`data-recurrence="series"`): render a small repeat/loop icon in the
bottom-right corner of the chip. The icon is 10x10px, `aria-hidden="true"`, colored with
`var(--scheduler-recurrence-indicator)`. It renders only when the chip is tall enough (height ≥
28px) to avoid overlap with the title; below that threshold it is hidden.

```
Tailwind classes (icon wrapper):
  absolute bottom-0.5 right-0.5 opacity-70 pointer-events-none
  [&>svg]:w-2.5 [&>svg]:h-2.5
  color: var(--scheduler-recurrence-indicator)
```

**Exception occurrence chips** (`data-recurrence="exception"`): same icon position, but:
- Icon color: `var(--scheduler-recurrence-exception)` (muted, to signal modification)
- Chip border: replace the accent-color left border with a dashed border:
  `border border-dashed` using `var(--scheduler-recurrence-exception)` for the border color.
  The solid left accent border (`border-l-2`) is replaced by the dashed border on all sides.

```
Tailwind (exception chip override):
  border border-dashed border-[var(--scheduler-recurrence-exception)] rounded
  (remove: border-l-2 border-l-[accentColor])
```

**Non-recurring chips:** no icon, no dashed border. Existing styling unchanged.

### R-3. Occurrence vs series choice dialog (W-SCHED-R1)

The choice dialog inherits the existing PopupEditor visual language for consistency.

**Dialog container:**
```
fixed inset-0 z-50 flex items-center justify-center bg-background/60 backdrop-blur-sm
```

**Dialog panel:**
```
w-80 rounded-lg border border-border bg-background shadow-xl p-5 flex flex-col gap-4
```

**Title:**
```
text-sm font-semibold text-foreground
```

**Body text (scope description):**
```
text-xs text-muted-foreground leading-relaxed
```

**Scope choice buttons (two primary choices):**
- Primary choice ("This event" / "Delete this event"):
  `w-full text-left px-3 py-2 rounded text-sm hover:bg-muted border border-border`
- Series choice ("All events" / "Delete all events"):
  `w-full text-left px-3 py-2 rounded text-sm hover:bg-muted border border-border`
- Both buttons are equal visual weight (no destructive coloring for the "all events" option;
  the choice is consequential but not irreversible at the data layer since the caller controls
  persistence).

**Cancel link (below the two choices):**
```
text-xs text-muted-foreground hover:text-foreground cursor-pointer mt-1 text-center
```

### R-4. Design tokens added by recurrence expansion

| Token | Default value | Usage |
|---|---|---|
| `--scheduler-recurrence-indicator` | `hsl(var(--primary))` | Repeat icon color on series chips |
| `--scheduler-recurrence-exception` | `hsl(var(--muted-foreground))` | Repeat icon + dashed border on exception chips |

Both tokens MUST be added to the design-token registry in `_shared/design/` when the recurrence
build cohort lands. They are forward-declared here so PAO can assign values during the polish pass.

### R-5. Agenda view recurrence indicator

In the Agenda view table, series occurrence rows SHOULD display a small repeat icon inline with the
event title (before the title text). Same icon, 10x10px, `aria-hidden="true"`, colored with
`var(--scheduler-recurrence-indicator)`. Exception rows use `var(--scheduler-recurrence-exception)`.

```
Tailwind (inline icon in agenda title cell):
  inline-block w-2.5 h-2.5 mr-1 opacity-70 shrink-0 align-middle
```
