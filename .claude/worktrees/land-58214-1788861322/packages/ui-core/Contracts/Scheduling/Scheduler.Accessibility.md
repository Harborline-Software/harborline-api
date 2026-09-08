# Scheduler — Accessibility Contract

- **Component:** Scheduler
- **ADR 0017 family:** Scheduling
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Scheduler.Semantic.md) · [Interaction](./Scheduler.Interaction.md) · [Styling](./Scheduler.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #114 Scheduler (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Scheduler baseline)

---

## 1. Navigation buttons

Previous/Next/Today buttons are standard `<button>` elements with descriptive `aria-label` values.

---

## 2. View switcher

View buttons are `role="tab"` within a `role="tablist"`. Active view has `aria-selected="true"`.

---

## 3. Time grid

Day/week time grid: `role="grid"` or `role="table"`. Time slot cells have `aria-label="{time}"`. Event chips have descriptive accessible names: `{title}: {start} to {end}`.

---

## 4. Keyboard navigation

- Arrow keys navigate between time slots and events in the grid.
- Enter activates an event (fires `onEventClick`).
- Delete/Backspace on selected event fires `onEventDelete`.

---

## 5. Drag operations

Drag-to-create and drag-to-move are not accessible via keyboard alone (G-SCHED-A1).

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SCHED-A1 | High | Drag operations (create/move/resize) are pointer-only — no keyboard equivalent | Accepted-risk M1; keyboard modal (Enter to create/edit) mitigates for AT users |

---

## Recurrence accessibility — 2026-06-12

> **Wave tag:** W-SCHED-R1

### R-1. Recurrence indicator labeling on occurrence chips

Series occurrence chips and exception chips carry visual recurrence indicators (see Styling §R-4).
Each MUST have a supplementary accessible label so screen readers announce the recurrence nature.

**Series occurrence chip accessible name pattern:**

```
"{title}: {start} to {end}, recurring event"
```

Implement via `aria-label` on the chip element. The trailing ", recurring event" suffix MUST be
appended for any chip with `data-recurrence="series"` or `data-recurrence="exception"`.

**Exception occurrence chip accessible name pattern:**

```
"{title}: {start} to {end}, recurring event (modified)"
```

The "(modified)" suffix distinguishes an individually edited occurrence from the series default.
Implement via `aria-label` on chips with `data-recurrence="exception"`.

**Recurrence indicator icon:** If the implementation renders an icon (e.g. a repeat/refresh glyph),
it MUST be `aria-hidden="true"`. The accessible name on the chip element (above) carries the full
announcement; a duplicate icon label would be verbose.

### R-2. Occurrence vs series choice dialog (W-SCHED-R1)

The scope-choice dialog (Interaction §R-2) MUST meet these ARIA requirements:

- The dialog container: `role="dialog"`, `aria-modal="true"`.
- Accessible name: `aria-labelledby` pointing to the dialog title element (e.g. "Edit recurring
  event" or "Delete recurring event").
- Accessible description: `aria-describedby` pointing to the body text that explains the two
  scopes ("Do you want to change only this event, or all events in the series?").
- Focus management: when the dialog opens, focus MUST move to the first interactive element
  (typically the "This event" button or the cancel button, per visual design order). When the
  dialog closes (any reason: choice made, cancelled, Escape), focus MUST return to the event chip
  that triggered the action.
- Escape key MUST close the dialog without action (same as Cancel).
- The two scope buttons: standard `<button>` elements; no ARIA role override needed.

**Focus trap:** while the dialog is open, Tab/Shift-Tab MUST cycle only within the dialog.
Clicks outside the dialog MUST close it (same as Cancel) and return focus.

### R-3. Recurrence icon in agenda view

In the Agenda view (table rows), series occurrences SHOULD display the recurrence icon in the
title cell. The icon is `aria-hidden="true"`. The row's first cell (date/time area) SHOULD carry
a `title` attribute with the accessible description:

```html
<td title="Recurring event">{dateText}</td>
```

This provides a supplementary tooltip for sighted keyboard users without adding screen-reader noise
(the chip `aria-label` in grid views already handles AT users).

### R-4. Known gaps

| Gap ID | Wave | Severity | Description | Disposition |
|---|---|---|---|---|
| G-SCHED-AA1 | W-SCHED-R1 | High | Choice dialog focus trap and return-focus not implemented until dialog is built | Spec READY; implement with dialog |
| G-SCHED-AA2 | W-SCHED-R2 | Medium | RecurrenceEditor inline form keyboard navigation (focus order within repeat fields) | Deferred with the editor |
| G-SCHED-AA3 | later | Low | Announcement of occurrence position in series ("occurrence 3 of 10") | Deferred; requires COUNT awareness at render time |

---

## Now-line + "Now" affordance accessibility — 2026-07-06

> **Wave tag:** CIC calendar-shape brief. Implemented in the React reference — documents shipped behavior.

### S-1. Now-line accessible text

The now-line indicator element (`data-now-indicator`) carries a `sr-only` child span with text
`"Current time, {time}"` (the `scheduler.nowSrText` i18n catalog key, `{time}` = the localized
current time). The visual dot + line are `aria-hidden="true"` — the sr-only span is the single
accessible-name source, avoiding a duplicate/verbose announcement.

### S-2. Today-column emphasis is not color-only

Today's column header is emphasized with `font-semibold` (a weight/token change), not a color
change alone — the distinction survives for colorblind users and in high-contrast themes. A
`data-today` attribute marks the header for test/tooling targeting.

### S-3. "Now" toolbar affordance

A standard `<button>` (no custom ARIA role needed) labelled via the `scheduler.now` catalog key.
Rendered conditionally (only while the now-line is off-screen) — screen readers encounter it in
the toolbar's normal tab order exactly when it's visually present; no `aria-hidden` gymnastics
needed for the hidden state since the element is not rendered at all when inactive.

### S-4. Off-hours / non-work-day shading is never color-only

Gutter hour labels for off-hours step down in font weight (`font-light` + a muted-foreground
tint), in addition to the background tint — so screen magnifier / low-vision users relying on
weight or the `data-out-of-hours` / `data-non-work-day` attributes (not just hue) can still
perceive the distinction. Shaded content remains fully in the tab order and scrollable — never
`aria-hidden`, `display: none`, or otherwise removed from the accessibility tree.

### S-5. Known gaps

| Gap ID | Wave | Severity | Description | Disposition |
|---|---|---|---|
| G-SCHED-AA4 | calendar-shape | Low | The "Now" affordance's visibility is driven by `IntersectionObserver`, which has no keyboard-only equivalent trigger (a keyboard user who has never scrolled will simply not see the button, same as a mouse user) | Accepted — the anchored-opening behavior (S-3 in the Semantic contract) already places the now-line in view on load for keyboard users in the common case |
