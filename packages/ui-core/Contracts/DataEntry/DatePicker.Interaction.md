# DatePicker — Interaction Contract

- **Component:** DatePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DatePicker.Semantic.md) · [Accessibility](./DatePicker.Accessibility.md) · [Styling](./DatePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DatePicker.tsx`
- **Catalog row:** #38 DatePicker (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Calendar button click** → `setOpen(!open)` (toggle)
- **Day selection** → closes calendar via `setOpen(false)` in `select()`
- **Blur on calendar container** → `if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false)` (focus leaves the popover div)

> **⚠ Implementation warning:** The `onBlur + contains(relatedTarget)` close pattern is broken on Safari — Safari does not set `relatedTarget` on non-interactive elements, causing the calendar to close before a day-click registers. Use a `useEffect` + `document.pointerdown` outside-ref listener instead, or rely on `@radix-ui/react-popover`'s `onOpenChange` which handles this correctly.

---

## 2. Date selection

- **Click a day button** → calls `select(date)` → updates value → `setOpen(false)`
- **"Today" button** → `new Date()` with hours set to 0 → calls `select(d)`
- **Type in DateInput** → calls `onValueChange(d | null)` directly; calendar cursor does not update (follows initial value only)

---

## 3. Month navigation

`‹` button → `setCursor(new Date(year, month - 1, 1))`

`›` button → `setCursor(new Date(year, month + 1, 1))`

Cursor is initialized to `value ?? new Date()`.

---

## 4. Disabled dates

Calendar day buttons are `disabled` when:
- `min && date < min`
- `max && date > max`
- `isDisabled(date, disabledDates)` returns true (array membership by `toDateString()`, or predicate)

Disabled buttons cannot be clicked.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DP1 | High | Calendar has no keyboard navigation (ArrowUp/Down/Left/Right between days) | Accepted-risk M1 |
| G-DP2 | Medium | `calendarView='year'` and `'decade'` are not implemented | Accepted-risk M1 |
| G-DP3 | Medium | Typing in the DateInput does not update the calendar cursor | Accepted-risk M1 |
| G-DP4 | Low | Calendar closes on any focus-leave from the container div — clicking outside the popover on a non-focusable element does not close it | Accepted-risk M1 |

> **Keyboard path (compensating requirement):** Until G-DP1 is resolved, keyboard-only users must be able to set a date via the DateInput text field. This requires G-DI2 (DateInput locale-aware decimal-separator validation) to ship together with DatePicker to ensure AT users receive validation feedback when typing a date. G-DP1 and G-DI2 are co-dependent for keyboard accessibility.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (open/onOpenChange; onFocus/onBlur).
> Supersedes: G-DP1 (keyboard nav in calendar — RESOLVED via Calendar Wave-N Interaction expansion; G-DP1 is no longer accepted-risk but REQUIRED), G-DP2 (year/decade views — REQUIRED at Wave-N), G-DP4 (blur/close Safari bug — replaced by onOpenChange pattern).

### 6. open / onOpenChange (FR-2 — replaces ad-hoc setOpen)

The popup open state is now managed via the `open` / `onOpenChange` contract surface. Internal behavior maps as follows:

| Trigger | onOpenChange |
|---|---|
| Calendar toggle button click | `onOpenChange(!current)` |
| Day selection | `onOpenChange(false)` |
| `Escape` key while popup is open | `onOpenChange(false)` |
| Focus leaves composite (both input and popup) | `onOpenChange(false)` |
| `Alt+Down` on focused input | `onOpenChange(true)` |
| Outside pointer-down | `onOpenChange(false)` |

The Safari `onBlur + contains(relatedTarget)` issue documented in §1 is resolved by adopting the `onOpenChange` pattern with an outside-pointer-down listener. The existing warning in §1 is superseded.

### 7. Calendar keyboard navigation (resolves G-DP1)

The internal Calendar sub-component now implements the full arrow-key grid navigation from the Calendar Wave-N Interaction expansion. G-DP1 ("No keyboard navigation within the calendar grid") is RESOLVED at Wave-N — it is no longer accepted-risk but a ship requirement.

### 8. Year / decade view interaction (resolves G-DP2)

`calendarView='year'` and `'decade'` are now functional. Header button click advances view. Escape returns to month view. Year/decade cell selection narrows view level. Calendar Wave-N Interaction §7 describes the keyboard mechanics.

### 9. focusedDate

When the popover opens and `focusedDate` is set, the calendar cursor initializes to `focusedDate` rather than `value ?? new Date()`. This allows hosts to open the calendar at an arbitrary date without changing the selected value.

### 10. onFocus / onBlur

`onFocus` and `onBlur` surface at the outer DatePicker container. They do NOT close the popup on blur — close is exclusively via `onOpenChange` triggers listed in §6.
