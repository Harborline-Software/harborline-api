# Scheduler — Interaction Contract

- **Component:** Scheduler
- **ADR 0017 family:** Scheduling
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Scheduler.Semantic.md) · [Accessibility](./Scheduler.Accessibility.md) · [Styling](./Scheduler.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #114 Scheduler (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Scheduler baseline)

---

## 1. Navigation

Previous/next buttons advance by one view period. "Today" button jumps to the current date. Date picker allows jumping to a specific date.

---

## 2. View switching

Toolbar buttons switch between `views`. Active view updates `view` prop (controlled) or internal state (uncontrolled).

---

## 3. Event create

When `editable=true`: click-and-drag on the time grid creates a new event spanning the dragged time range. Calls `onEventCreate({ start, end })`. Caller is responsible for adding the event to `events`.

---

## 4. Event drag-to-move

Drag an existing event to a new time slot. Calls `onEventUpdate(updatedEvent)` with new `start`/`end`. Caller updates `events`.

---

## 5. Event resize

Drag the event's bottom edge to resize duration. Calls `onEventUpdate(updatedEvent)`.

---

## 6. Event click

Click an event: calls `onEventClick(event)`. Caller renders a detail popup or navigates.

---

## 7. Event keyboard delete

When an event is focused (via click or keyboard navigation) and the keyboard `Delete` or `Backspace` key is pressed: fires `onEventDelete(event)`. The caller is responsible for removing the event from `events`. This behavior is active only when `editable=true`.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SCHED1 | High | Drag operations (create, move, resize) are pointer-only — no keyboard equivalent | Accepted-risk M1; forward-spec baseline; keyboard event interaction requires further design in M2 |
| G-SCHED2 | Medium | "Selected event" state is not defined in Semantic — no `selectedEvent` prop or `onEventSelect` callback; event focus is implicit via `onEventClick` only | Fix-deferred M2; add explicit selection model |

---

## Polish expansion (2026-06-11 — Polish-pilot, see _shared/design/polish-gate.md)

Reference: SVAR React Event Calendar. REQUIRED additions:

1. **Drag-create.** In day/week views, pointer-drag across empty slots
   previews a range and fires `onEventAdd({ start, end })` on release.
2. **Drag-move.** Dragging an event chip moves it between slots/days,
   snapping to the slot grid; fires `onEventUpdate(event, { start, end })`.
   Controlled — no internal mutation.
3. **Popup editor.** Double-click an event opens a small popup editor
   (title, start, end); Save → `onEventUpdate`, on empty-slot double-click →
   editor in create mode → `onEventAdd`. Escape closes.
4. **Month view events.** Month cells render up to 3 event chips + a
   "+N more" overflow row; clicking overflow switches to that day's Day view.
5. **Now indicator.** In day/week views a current-time line renders across
   today's column (TimeProvider-friendly: derive from a `now?: Date` prop
   defaulting to render-time).
6. **Working hours.** `workingHours?: { start: number; end: number }`
   shades out-of-hours slots.
7. `readOnly` disables 1–3 but keeps all rendering.

---

## Recurrence interaction — 2026-06-12

> **Wave tags:**
> - W-SCHED-R1 — occurrence vs series choice dialogs (edit + delete flows)
> - W-SCHED-R2 — drag-move occurrence exception creation; popup editor recurrence tab

### R-1. Callback back-compat: extended event shapes

The existing `onEventAdd`, `onEventUpdate`, and `onEventDelete` callbacks are the sole mutation
surface. Their signatures are extended with optional recurrence fields; the caller's existing
handlers that ignore the new fields continue to work without change.

**`onEventAdd` — no change needed.** New series masters are created by the caller; `<Scheduler>`
fires `onEventAdd` only for drag-create, which always creates a non-recurring single event.

**`onEventUpdate` extended payload shape:**

```typescript
// The same SchedulerEvent interface now carries recurrence fields (see Semantic §R-1).
// onEventUpdate receives the full event object — callers that read only title/start/end
// continue to work; callers that want to persist recurrence data read the new fields.
onEventUpdate?: (event: SchedulerEvent) => void
// Unchanged signature; new recurrence fields present on event when editing a series master
// or when an occurrence is promoted to an exception (recurrenceId + originalStart set).
```

**`onEventDelete` extended to identify scope:**

```typescript
// Replaces the prior (id: string|number) signature to carry full event context.
// Back-compat: callers can read event.id for deletion as before.
onEventDelete?: (event: SchedulerEvent) => void
```

The scope of the deletion (one occurrence vs. whole series) is communicated by the event object the
callback receives, not by a separate flag:

- Delete whole series: `event` is the series master (has `recurrenceRule`, no `recurrenceId`).
  Caller removes the master from the data array; all occurrences disappear.
- Delete one occurrence: `event` is a series master UPDATED with an additional exception date appended
  to `recurrenceExceptions`. Caller updates (not deletes) the master in the data array. The
  component produces this by calling `onEventUpdate` (not `onEventDelete`) with the patched master.
  See §R-1a below for the exact sequence.

**§R-1a — delete-occurrence sequence (W-SCHED-R1):**

When the user deletes a single occurrence from a recurring series (via Delete/Backspace key or the
delete button in the popup editor), and the occurrence choice dialog resolves to "this event":

1. Compute `excludedStart`: the `start` of the occurrence being deleted.
2. Produce an updated master: `{ ...master, recurrenceExceptions: [...(master.recurrenceExceptions ?? []), excludedStart] }`.
3. Call `onEventUpdate(updatedMaster)`.
4. Do NOT call `onEventDelete`.

This keeps the deletion model consistent: the data array always changes by updating the master's
exception list, never by removing individual occurrence records (which are virtual/expanded, not
stored).

When the user deletes an existing exception record (a previously moved/edited single occurrence):

- If scope is "this event": call `onEventDelete(exceptionRecord)`. The caller removes that exception
  record. The occurrence re-appears via normal expansion at its original time.
- If scope is "all events in series": call `onEventDelete(master)`. The caller removes the master
  and all associated exception records.

### R-2. Occurrence vs series choice dialog (W-SCHED-R1)

When the user initiates an edit (double-click, popup editor save) or delete (Delete key, popup
delete button) on an event chip that is a rendered series occurrence (i.e. the underlying event has
`recurrenceRule` set), the component MUST display a scope-choice dialog before firing any callback.

The dialog is internal to `<Scheduler>` and is NOT a separate exported component in v1
(deferred to W-SCHED-R2 for customization API).

**Edit scope dialog:**

- Title: "Edit recurring event"
- Choices: "Edit this event" (default) | "Edit all events in the series"
- Cancel button closes without action.
- "Edit this event": opens the popup editor pre-filled with the occurrence's data. On Save, fires
  `onEventUpdate` with an exception record (recurrenceId + originalStart set).
- "Edit all events in the series": opens the popup editor pre-filled with the series master's data.
  On Save, fires `onEventUpdate` with the modified master. All occurrences reflect the change.

**Delete scope dialog:**

- Title: "Delete recurring event"
- Choices: "Delete this event" (default) | "Delete all events in the series"
- Cancel button closes without action.
- "Delete this event": fires `onEventUpdate(masterWithExceptionAdded)` as described in §R-1a.
- "Delete all events in the series": fires `onEventDelete(master)`.

**When to show the dialog:**

| Trigger | Target event type | Show dialog? |
|---|---|---|
| Edit (double-click / popup Save) | non-recurring event | No — fire callback directly |
| Edit | series occurrence (from expansion) | Yes — edit scope dialog |
| Edit | existing exception record | Yes — edit scope dialog |
| Delete (key / button) | non-recurring event | No — fire callback directly |
| Delete | series occurrence | Yes — delete scope dialog |
| Delete | existing exception record | Yes — delete scope dialog |

**Exception record produced by "Edit this event":**

```typescript
// Scheduler produces this shape and passes it to onEventUpdate:
const exceptionRecord: SchedulerEvent = {
  ...occurrenceSnapshot,         // title, color, etc. as they were at the time of the occurrence
  recurrenceId: master.id,       // links back to the series master
  originalStart: occurrence.start, // which occurrence slot this replaces
  recurrenceRule: undefined,     // exceptions are NOT themselves recurring
  recurrenceExceptions: undefined,
}
// The caller is responsible for adding this to the data array alongside the unchanged master.
```

### R-3. Drag-move occurrence exception creation (W-SCHED-R2)

When the user drags a series occurrence chip to a new time slot (drag-move interaction per Polish
expansion §2 above), and the occurrence choice dialog resolves to "move this event":

1. Compute the new `start` and `end` from the drop position.
2. Fire `onEventUpdate` with an exception record (shape as in §R-2) where:
   - `originalStart` is the occurrence's pre-drag start
   - `start` / `end` are the new post-drag times

When the dialog resolves to "move all events in the series":
1. Shift the master's `start` by the drag delta (preserving the day-of-week and duration pattern).
2. Fire `onEventUpdate(updatedMaster)`.

This interaction is wave W-SCHED-R2; it requires the choice dialog (W-SCHED-R1) to be built first.

### R-4. Popup editor recurrence tab (W-SCHED-R2)

The popup editor (Polish expansion §3) gains a "Repeat" section below the start/end fields. This
section renders a RecurrenceEditor-equivalent inline form (no external import in v1) that produces
a bounded-subset RRULE string (Semantic §R-2). The complete field set:

- Repeat frequency selector: None | Daily | Weekly | Monthly | Yearly
- Interval: number input (1–99); label adapts: "day(s)" / "week(s)" / "month(s)" / "year(s)"
- For Weekly: day-of-week checkboxes (Mo Tu We Th Fr Sa Su)
- For Monthly: "On day N of the month" (BYMONTHDAY) OR "On the Nth WEEKDAY" (BYDAY ordinal)
- For Yearly: month selector + day-of-month input (BYMONTH + BYMONTHDAY)
- End: Never | After N occurrences (COUNT) | On date (UNTIL)

When the user saves with a frequency other than "None": `onEventUpdate` (or `onEventAdd` for new
events) receives the event with `recurrenceRule` set to the generated RRULE string. When saving with
"None": `recurrenceRule` is `undefined`.

This section is deferred to W-SCHED-R2; the base popup editor (already shipped) does not include
it.

### R-5. Known gaps

| Gap ID | Wave | Severity | Description | Disposition |
|---|---|---|---|---|
| G-SCHED-RI1 | W-SCHED-R1 | High | Choice dialogs not implemented | Spec READY; implement in recurrence build cohort |
| G-SCHED-RI2 | W-SCHED-R1 | High | Delete-occurrence produces onEventUpdate not onEventDelete — callers must handle | Breaking change from naive deletion; document in migration notes at build time |
| G-SCHED-RI3 | W-SCHED-R2 | Medium | Drag-move creates exception — multi-day cross-boundary drag deferred | Accepted-risk; single-day drag only in v1 |
| G-SCHED-RI4 | W-SCHED-R2 | Medium | RecurrenceEditor inline form in popup | Deferred; callers set recurrenceRule externally in v1 |
| G-SCHED-RI5 | later | Low | "Edit from this occurrence forward" (third scope option) | Deferred; requires splitting master RRULE at a date, complex |
