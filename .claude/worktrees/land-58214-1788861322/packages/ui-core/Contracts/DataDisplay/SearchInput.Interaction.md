# SearchInput — Interaction Contract

- **Component:** SearchInput
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchInput.Semantic.md) · [Interaction](./SearchInput.Interaction.md) · [Accessibility](./SearchInput.Accessibility.md) · [Styling](./SearchInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SearchInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
[empty]
  │ user types → [has-value, debounce timer running]
  │
[has-value, debounce timer running]
  ├── timer fires → onChange(value) called → [has-value, idle]
  ├── user types more → timer restarted
  └── user clicks ✕ / handleClear → onChange('') called immediately → [empty]

External value change (useEffect):
  any state ──> setLocalValue(value) ──> re-render with new value
```

---

## 2. Keystroke handling

| User action | Local state | Timer | onChange |
|---|---|---|---|
| Types a character | `localValue` updates immediately | Cleared and restarted | Not called yet |
| Pauses typing for `debounceMs` | unchanged | Fires | Called with current `localValue` |
| Types in rapid succession | Updates on each key | Kept restarting | Not called during typing |

### Debounce loading indicator

While a debounce timer is running (user has typed but `onChange` has not yet
fired), SearchInput should set `aria-busy="true"` on the wrapper element.
When the timer fires and `onChange` is called, `aria-busy` is removed (set to
`false` or omitted).

This allows AT to announce "busy" state to users who rely on screen readers —
without it, there is a silent window between keystroke and result update that
AT cannot communicate.

The `aria-busy` attribute is placed on the outer search wrapper div, not the
`<input>` element itself (placing it on `<input>` is non-standard and ignored
by some ATs).

---

## 3. Clear button behaviour

| Condition | Clear button visible |
|---|---|
| `localValue` is empty or `''` | Hidden (conditional render) |
| `localValue` has any content | Shown |

Clicking clear:
1. `setLocalValue('')`
2. `onChange('')` fires immediately (no debounce)
3. Pending debounce timer cleared

---

## 4. External value sync

When the `value` prop changes (e.g., host applies a saved view or clears all filters):
- `useEffect` updates `localValue` to the new `value`.
- Any pending debounce timer fires when it was scheduled and will call `onChange` with the OLD local value.

**Known gap (I1):** If the host changes `value` while a debounce timer is pending, the timer fires and calls `onChange` with the stale pre-sync local value, potentially overwriting the host's programmatic set. This is an edge case but can cause search-clear flicker.

---

## 5. Keyboard behaviour

| Key | Action |
|---|---|
| Any printable key | Updates `localValue`; starts/restarts debounce timer |
| Backspace / Delete | Updates `localValue`; clears if all text removed |
| Tab | Moves focus to next element |
| Escape | Not bound (gap — see §6.I2) |

---

## 6. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | Stale debounce timer can fire after external value sync | Rare edge case; harmless in most list-filter patterns |
| I2 | Escape key does not clear the input | Inconsistent with common search UX (Cmd+K / Esc pattern) |
| I3 | Timer not cancelled on `value` prop change | See I1 |
