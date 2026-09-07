# Scheduler — Semantic Contract

- **Component:** Scheduler
- **ADR 0017 family:** Scheduling
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./Scheduler.Interaction.md) · [Accessibility](./Scheduler.Accessibility.md) · [Styling](./Scheduler.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Scheduler / KendoReact Scheduler)
- **Catalog row:** #114 Scheduler (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Scheduler baseline)

- **Reference API (Kendo minimum-surface):** https://www.telerik.com/kendo-react-ui/components/scheduler/api
- **SchedulerModelFields audit:** `recurrenceRule`, `recurrenceId`, `recurrenceExceptions` all confirmed present in Kendo's SchedulerModelFields interface (fetched 2026-06-12)

---

## 1. Component purpose

**Scheduler** — a calendar-based event scheduling component with multiple view modes (day, week, month, agenda). Used for recurring maintenance scheduling, booking surfaces, and work-order timelines. Renders events on a time grid and supports drag-to-create and drag-to-move operations.

---

## 2. Props (planned)

```typescript
interface SchedulerEvent {
  id: string | number
  title: string
  start: Date
  end: Date
  allDay?: boolean
  resource?: string | number    // for resource grouping
  color?: string
  [key: string]: unknown        // extensible event data
}

interface SchedulerProps {
  events: SchedulerEvent[]
  defaultDate?: Date              // initial focused date; default: new Date()
  date?: Date                     // controlled focused date
  onDateChange?: (date: Date) => void
  view?: 'day' | 'week' | 'month' | 'agenda'    // default: 'week'
  views?: Array<'day' | 'week' | 'month' | 'agenda'>  // available views; default: all
  onViewChange?: (view: string) => void
  onEventCreate?: (event: Partial<SchedulerEvent>) => void
  onEventUpdate?: (event: SchedulerEvent) => void
  onEventDelete?: (event: SchedulerEvent) => void
  onEventClick?: (event: SchedulerEvent) => void
  step?: number                  // slot duration in minutes; default: 30
  startTime?: string             // day start HH:mm; default: '00:00'
  endTime?: string               // day end HH:mm; default: '24:00'
  editable?: boolean             // default: true
  className?: string
}
```

---

## 3. View modes

**day**: single-day column with time slots.
**week**: 7-day columns with time slots.
**month**: monthly calendar grid with event chips per day.
**agenda**: flat list of upcoming events sorted by date.

---

## 4. Event display

Events render as colored chips in the time grid (day/week) or as small bars in month view. `allDay` events render in a separate all-day row at the top of day/week views.

---

## 5. Timezone

Scheduler renders all times in the browser's local timezone by default. Timezone support is out-of-scope for v1.

---

## 6. Time-range composition (`step`, `startTime`, `endTime`)

These three props together define the visible time grid in `'day'` and `'week'` views. `'month'` and `'agenda'` views ignore them.

| Prop | Type | Default | Effect |
|---|---|---|---|
| `startTime` | `string` (`'HH:mm'`) | `'00:00'` | First slot visible at the top of the day column |
| `endTime` | `string` (`'HH:mm'`) | `'24:00'` | Last slot visible at the bottom; use `'24:00'` for end-of-day |
| `step` | `number` (minutes) | `30` | Slot height granularity — each time slot spans `step` minutes |

**Composition example** — a 9 AM–6 PM business-hours view with 15-minute slots:

```tsx
<Scheduler
  startTime="09:00"
  endTime="18:00"
  step={15}
  events={events}
/>
```

Events that start or end outside the `[startTime, endTime]` range are clipped visually but remain in the data model. Scrolling to bring clipped events into view is out-of-scope for v1.

---

## Recurrence expansion — 2026-06-12

> **Wave tag:** W-SCHED-R1 (data model + field mapping + expansion contract). Implementation deferred
> to the recurrence build cohort; this section is the RED-first spec that cohort consumes.

### R-1. Recurrence data model

The `SchedulerEvent` interface gains four recurrence fields. All are optional so non-recurring events
require zero changes to existing call sites.

```typescript
interface SchedulerEvent {
  id: string | number
  title: string
  start: Date
  end: Date
  allDay?: boolean
  resource?: string | number
  color?: string
  description?: string
  // --- W-SCHED-R1: recurrence fields ---
  recurrenceRule?: string          // RFC 5545 RRULE string, see §R-2 for v1 bounded subset
  recurrenceId?: string | number   // present on EXCEPTION records only; equals the series master's id
  recurrenceExceptions?: Date[]    // series master only; dates of excluded/overridden occurrences
  originalStart?: Date             // exception record only; the occurrence start this record replaces
  [key: string]: unknown
}
```

**Record taxonomy:**

| Record type | `recurrenceRule` | `recurrenceId` | `recurrenceExceptions` | `originalStart` |
|---|---|---|---|---|
| Non-recurring event | absent | absent | absent | absent |
| Series master | present | absent | may be present (list of excluded start dates) | absent |
| Occurrence exception | absent | set to master's `id` | absent | set to the original occurrence `start` |

An occurrence exception represents a single occurrence that has been moved or edited independently of
its series. The scheduler renders the exception in place of the occurrence that would normally fall
at `originalStart`, and excludes that date from the master's expansion.

### R-2. RRULE v1 bounded subset

The v1 implementation MUST support this RRULE subset. Anything outside this list MUST be either
parsed-and-ignored (producing no occurrences) or surfaced as a validation warning — never silently
corrupt the series.

**Supported RRULE parts (v1):**

| Part | Supported values | Notes |
|---|---|---|
| `FREQ` | `DAILY`, `WEEKLY`, `MONTHLY`, `YEARLY` | Required when `recurrenceRule` is non-empty |
| `INTERVAL` | positive integer, default 1 | e.g. `INTERVAL=2` = every other week |
| `COUNT` | positive integer | number of occurrences; mutually exclusive with `UNTIL` |
| `UNTIL` | UTC datetime string (`YYYYMMDDTHHmmssZ`) | inclusive end; mutually exclusive with `COUNT` |
| `BYDAY` | comma-separated two-letter day codes (`MO`, `TU`, `WE`, `TH`, `FR`, `SA`, `SU`); optional ordinal prefix (`1MO`, `-1FR`) | valid for `FREQ=WEEKLY` and `FREQ=MONTHLY`; for MONTHLY the ordinal prefix is used (e.g. `1MO` = first Monday) |
| `BYMONTHDAY` | comma-separated day-of-month integers (1–28; negative ordinals deferred) | valid for `FREQ=MONTHLY` and `FREQ=YEARLY`; day 29/30/31 deferred (month-end handling varies) |
| `BYMONTH` | comma-separated month integers (1–12) | valid for `FREQ=YEARLY` |

**Deferred to later wave (do NOT implement in v1):**

- `BYSETPOS`, `BYHOUR`, `BYMINUTE`, `BYSECOND`, `BYWEEKNO`, `BYYEARDAY`
- Negative `BYMONTHDAY` ordinals (last day of month patterns)
- `BYMONTHDAY` day 29, 30, 31 (month-length edge cases)
- `WKST` (week-start override)
- Multi-value `FREQ` (not part of RFC 5545 but appears in some RRULE libraries)

**RRULE string format:** plain RRULE property value only — no `RRULE:` prefix, no `VEVENT` wrapper.
The value is the part after the colon in an iCalendar `RRULE:` line.

```
// Series: every weekday, ending after 10 occurrences
"FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;COUNT=10"

// Series: every first Monday of the month, until 2026-12-31
"FREQ=MONTHLY;BYDAY=1MO;UNTIL=20261231T235959Z"

// Series: annually on Jan 15
"FREQ=YEARLY;BYMONTH=1;BYMONTHDAY=15"

// Simple daily, every 2 days, no end
"FREQ=DAILY;INTERVAL=2"
```

This subset matches the surface confirmed in Kendo's SchedulerModelFields and RecurrenceEditor
documentation (fetched 2026-06-12). The Kendo RecurrenceEditor UI generates exactly this subset.

### R-3. Expansion contract

The expansion function is a pure function with no side effects. It MUST be exported from the
component package so consumers can pre-expand data on the server or in a worker.

```typescript
/**
 * Expand a recurring series master into concrete occurrence records
 * within the half-open range [rangeStart, rangeEnd).
 *
 * Returns an array of SchedulerEvent where each item:
 * - has the same id, title, color, and other fields as the master
 * - has start/end computed from the RRULE relative to the master's start
 * - has recurrenceId set to the master's id
 * - does NOT appear if its originalStart is listed in master.recurrenceExceptions
 *
 * Exception override records (those with recurrenceId set) are NOT returned
 * by this function — callers merge them in separately after expansion.
 *
 * Throws RangeError if rangeEnd <= rangeStart.
 * Returns [] for non-recurring events (no recurrenceRule).
 * Returns [] for invalid or unsupported RRULE (log a console.warn with the
 *   raw rule string so the caller can debug; do not throw).
 */
export function expandRecurrence(
  master: SchedulerEvent,
  rangeStart: Date,
  rangeEnd: Date,
): SchedulerEvent[]
```

**Caller merge pattern** (this is the expected consumer pattern, not part of the function contract):

```typescript
// 1. Separate masters, exceptions, and non-recurring events
const masters    = data.filter(e => e.recurrenceRule)
const exceptions = data.filter(e => e.recurrenceId)
const singles    = data.filter(e => !e.recurrenceRule && !e.recurrenceId)

// 2. Expand each master within the visible range
const expanded = masters.flatMap(m => expandRecurrence(m, rangeStart, rangeEnd))

// 3. Merge: replace expanded occurrences that have a matching exception
//    (match on recurrenceId === master.id AND originalStart === occurrence.start)
const occurrenceMap = new Map(expanded.map(o => [`${o.recurrenceId}:${o.start.toISOString()}`, o]))
exceptions.forEach(ex => {
  const key = `${ex.recurrenceId}:${ex.originalStart!.toISOString()}`
  occurrenceMap.set(key, ex)   // overwrite expanded occurrence with exception record
})

const allOccurrences = Array.from(occurrenceMap.values())
const rendered = [...singles, ...allOccurrences]
```

**Expansion constraints:**

- The function MUST NOT expand more than 1000 occurrences per call (guard against infinite series
  flooding the renderer). If COUNT is absent and UNTIL is absent, cap at 1000.
- For `FREQ=MONTHLY` with `BYDAY` ordinal (e.g. `1MO`), if the computed day does not exist in a
  given month (e.g. fifth Monday), skip that month silently.
- Duration of each expanded occurrence equals the master's `end - start` duration.
- `allDay` flag is inherited from the master.

### R-4. `modelFields` prop — field name remapping

Mirrors Kendo's `SchedulerModelFields` interface. Allows callers whose data uses different property
names to avoid transforming the array before passing it to `<Scheduler>`.

```typescript
interface SchedulerModelFields {
  id?:                  string  // default: 'id'
  title?:               string  // default: 'title'
  start?:               string  // default: 'start'
  end?:                 string  // default: 'end'
  allDay?:              string  // default: 'allDay'
  description?:         string  // default: 'description'
  color?:               string  // default: 'color'
  // W-SCHED-R1: recurrence field mappings
  recurrenceRule?:      string  // default: 'recurrenceRule'
  recurrenceId?:        string  // default: 'recurrenceId'
  recurrenceExceptions?: string // default: 'recurrenceExceptions'
  originalStart?:       string  // default: 'originalStart'
}
```

The `<Scheduler>` component MUST read all recurrence fields through these aliases when `modelFields`
is provided. The `expandRecurrence` function always receives already-normalized `SchedulerEvent`
objects (the caller applies field mapping before calling expand).

```typescript
// SchedulerProps addition:
modelFields?: SchedulerModelFields
```

When `modelFields` is absent, the component reads the canonical field names listed in `SchedulerEvent`
above. When present, each alias replaces the corresponding canonical name for reads only — the shape
of callback payloads (`onEventUpdate`, `onEventAdd`, etc.) always uses canonical names.

### R-5. Recurrence indicator on event chips

Series occurrences (those produced by expansion, plus exception records) MUST carry a visual
recurrence indicator so the user knows the event is part of a series. The indicator is specified in
the Styling contract (§R-4). Exception records (moved/edited occurrences) MUST display the indicator
in a visually distinct state (dashed border or muted icon) to signal that this occurrence has been
individually modified.

**Data attributes for test targeting:**

- `data-recurrence="series"` — on chips rendered from expansion (recurrenceId present, not an exception)
- `data-recurrence="exception"` — on chips rendered from exception records (recurrenceId present AND
  originalStart present AND differs from current start)
- No `data-recurrence` attribute on non-recurring event chips

### R-6. Known gaps and deferred work

| Gap ID | Wave | Description | Disposition |
|---|---|---|---|
| G-SCHED-R1 | W-SCHED-R1 | `expandRecurrence` unit-test suite (pure function; RED-first) | Implement in build cohort |
| G-SCHED-R2 | W-SCHED-R2 | RecurrenceEditor UI widget (RRULE builder form inside popup editor) | Deferred; v1 callers set recurrenceRule string externally |
| G-SCHED-R3 | W-SCHED-R2 | Occurrence vs series choice dialog (edit/delete scope prompt) | See Interaction §R-2 |
| G-SCHED-R4 | later | Timezone-aware RRULE expansion (DTSTART with TZID) | Out-of-scope v1; Scheduler is local-timezone-only |
| G-SCHED-R5 | later | BYSETPOS, negative BYMONTHDAY, WKST, BYWEEKNO | Deferred RRULE parts |
| G-SCHED-R6 | later | Cross-day dragging of occurrence (changes DTSTART across day boundary) | Complex recurrence mutation; deferred |

---

## Working-hours-aware Day/Week + now-line + anchored opening — 2026-07-06

> **Wave tag:** CIC calendar-shape brief. Implemented in the React reference (`packages/ui-react/src/components/datagrid/Scheduler.tsx`) — this section documents the shipped behavior (not forward-spec, unlike the rest of this contract).

### S-1. Kendo-parity working-day props (additive)

```typescript
interface SchedulerProps {
  // ... existing props ...
  /** Legacy working-hours shading (start/end hour only). Wins over workDayStart/workDayEnd
   *  when both are supplied (back-compat with pre-2026-07-06 callers). */
  workingHours?: SchedulerWorkingHours
  /** Kendo-parity working-day prop. Default: 8 (8 AM). */
  workDayStart?: number
  /** Kendo-parity working-day prop. Default: 18 (6 PM). */
  workDayEnd?: number
  /** Which weekdays are unshaded work days (`Date#getDay()` convention). Default: Mon–Fri
   *  (`[1, 2, 3, 4, 5]`). */
  workDays?: SchedulerWorkDay[]
}
```

These are additive per the feature-complete doctrine — no existing prop was removed or narrowed.
`workingHours` is honored for back-compat; new call sites should prefer `workDayStart`/`workDayEnd`.

### S-2. Now-line

Day/week views render an accent horizontal line + a small time chip in the hour gutter on
TODAY's column only. When the `now` prop is omitted (the common case), the Scheduler ticks an
internal clock every minute (a single `setInterval`, cleaned up on unmount/prop-change) so the
line tracks real time with zero consumer wiring. Passing `now` explicitly freezes it (tests,
storybook, fixed-time demos).

### S-3. Anchored opening

On mount and on every view/date navigation, the day/week scroll body is instantly repositioned
(no scroll animation — motion is reserved for the explicit "Now" affordance click):
- If today is in view: the now-line anchors ~1/3 of the viewport down from the top.
- Otherwise: `workDayStart`'s hour row anchors flush to the top.

A "Now" toolbar affordance (labelled via the `scheduler.now` i18n catalog key) renders only
while the now-line has scrolled off-screen (tracked via `IntersectionObserver`); clicking it
smooth-scrolls back to the anchor (instant under `prefers-reduced-motion`). Free scrolling is
never hijacked — no CSS scroll-snap, no scroll-position enforcement outside these two triggers.

### S-4. Non-work-day shading (week view)

Week view shades whole columns for weekdays not in `workDays` (e.g. Sat/Sun for the Mon-Fri
default) — always scrollable, never collapsed. Off-hours (outside `workDayStart`/`workDayEnd`)
shade individual hour rows in every day/week column, same as the legacy `workingHours` behavior.

### S-5. Known gaps

| Gap ID | Wave | Description | Disposition |
|---|---|---|---|
| G-SCHED-S1 | calendar-shape | `weekStart()` is still Sunday-first regardless of locale (pre-existing; not touched by this wave) | Deferred — a genuine locale-aware week-start is a separate, larger effort |
| G-SCHED-S2 | calendar-shape | Date/time formatting throughout the component is hardcoded to the `'en'` locale (pre-existing) | Deferred — out of scope for the now-line/anchoring wave |
