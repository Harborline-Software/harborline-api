# TimePicker — Interaction Contract

- **Component:** TimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TimePicker.Semantic.md) · [Accessibility](./TimePicker.Accessibility.md) · [Styling](./TimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TimePicker.tsx`
- **Catalog row:** #137 TimePicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input handling

The component renders a single `<input type="time">`. All interaction (picker popup, keyboard arrow navigation, AM/PM toggle) is native browser behavior. The component only intercepts `onChange`.

`onChange` → `timeStringToDate(e.target.value)` → `onValueChange(Date | null)`.

---

## 2. State machine

```
value changes via native input
  → handleChange fires
  → if uncontrolled: setInternal(dateOrNull)
  → onValueChange?.(dateOrNull)
```

---

## 3. Keyboard (native `<input type="time">`)

Native browser time input keyboard handling: arrow keys cycle hours/minutes, typing digits, AM/PM toggle. Not customized in M1.

---

## 4. Min / max

`min` and `max` props are passed to the native input as `HH:mm` strings. Browser enforces the constraint UI (greying invalid times in the picker).

---

## 5. Format and steps

`format` and `steps` props are accepted by the interface but not consumed in M1's native `<input type="time">` approach. Reserved for a custom time-picker panel in a future wave.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TP1 | Medium | `format` and `steps` props are no-ops in M1 — UI only shows HH:mm regardless | Accepted-risk M1; documented as reserved |
| G-TP2 | Low | Native time picker appearance varies significantly across browsers and platforms | Accepted-risk M1; custom picker deferred |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (open/onOpenChange + onFocus/onBlur).
> Supersedes: G-TP1 partially resolved — steps maps to HTML step attribute in native path; format remains reserved.

### 7. nowButton interaction (Wave-N)

**Native-input path:** a "Now" button renders beside the `<input type="time">`. Click → `onValueChange(new Date())` with today's date and current H:M, seconds zeroed. This is a standalone button; it does not interact with the native time picker popup.

**Custom popup path:** "Now" appears in the popup footer row. Click → same behavior as native path.

### 8. steps — native path (Wave-N)

`steps.minute` and `steps.second` map to the `step` attribute as described in Semantic §6. The native browser uses this to: (a) constrain selectable times in the native picker dropdown, (b) determine how much `ArrowUp`/`ArrowDown` increment the time field. Example: `steps.minute=15` → `step=900` → browser increments/decrements in 15-minute steps.

### 9. open / onOpenChange (FR-2)

In the M1 native-input path: no-ops. The native browser manages its own time picker popup. `open` / `onOpenChange` are contract-reserved for the custom popup wave.

### 10. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` forward to the native `<input type="time">`. They fire for host focus-ring wiring and composite-widget blur detection.
