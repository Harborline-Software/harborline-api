# FilterChips — Interaction Contract

- **Component:** FilterChips
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterChips.Semantic.md) · [Interaction](./FilterChips.Interaction.md) · [Accessibility](./FilterChips.Accessibility.md) · [Styling](./FilterChips.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FilterChips.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

FilterChips is a controlled component. Selection state is external.

```
Host sets value ──────────> FilterChips re-renders
User clicks chip ─────────> toggle(v) ──> onChange(next) ──> Host updates value ──> re-render
```

---

## 2. Click behaviour

| `multiple` | Chip state before click | Action |
|---|---|---|
| `false` | unselected | `onChange(chipValue)` — selects this chip |
| `false` | selected | `onChange([])` — deselects (empty selection) |
| `true` | unselected | Adds to set → `onChange([...newSet])` |
| `true` | selected | Removes from set → `onChange([...newSet])` |

The component does NOT optimistically update — it re-renders after the host's `value` prop changes in response to `onChange`.

---

## 3. Keyboard behaviour

Each chip is a `<button type="button">`. Standard button keyboard behaviour applies:

| Key | Action |
|---|---|
| Tab / Shift+Tab | Move focus between chips |
| Enter / Space | Trigger `onClick` (same as click) |
| Arrow keys | Not bound — tab navigation is the model |

---

## 4. Focus behaviour

FilterChips uses natural tab order. No roving tabindex or arrow-key navigation is implemented. This is a known gap for a listbox-like component (see Accessibility §6).

---

## 5. Visual state on interaction

| State | Trigger | Visual |
|---|---|---|
| Selected | `selected.has(opt.value)` is true | `bg-blue-600 text-white` (chip + count) |
| Unselected | default | `bg-gray-100 text-gray-700` |
| Focus | keyboard / click | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| Hover (unselected) | pointer hover | `hover:bg-gray-200` |

---

## 6. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No arrow-key navigation within the listbox | `role="listbox"` implies arrow-key navigation per WAI-ARIA spec; only Tab is implemented |
| I2 | `onChange` signature is inconsistent between single/multi | Single deselect returns `[]` (array) not `undefined \| null`; callers must handle both `T` and `T[]` |
| I3 | No disabled chip state | All chips are always clickable |
