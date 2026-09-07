# FilterBar — Interaction Contract

- **Component:** FilterBar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterBar.Semantic.md) · [Interaction](./FilterBar.Interaction.md) · [Accessibility](./FilterBar.Accessibility.md) · [Styling](./FilterBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/FilterBar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

FilterBar is a display component. Its interaction surface is limited to chip removal and the "Clear all" affordance. All state lives in the host.

---

## 2. State machine

FilterBar has **no internal state**. It is fully controlled:

```
Host updates chips array ──> FilterBar re-renders
User clicks remove ──────────> onRemove(id) ──> Host updates chips ──> re-render
User clicks "Clear all" ─────> onClearAll() ──> Host clears chips ──> re-render (empty state)
```

---

## 3. Chip remove behaviour

| Interaction | Result |
|---|---|
| Click remove button (`✕`) on a chip | `onRemove(chip.id)` fires; component does NOT optimistically remove the chip — re-render depends on host updating `chips` |
| Click remove on a `removable: false` chip | No remove button rendered; no interaction possible |
| Click remove when `onRemove` not provided | No remove button rendered |

---

## 4. Clear all behaviour

The "Clear all" button is rendered only when BOTH:
- `onClearAll` prop is provided, AND
- `chips.length > 1`

Clicking "Clear all" fires `onClearAll()`. The host is responsible for setting `chips` to `[]`.

---

## 5. Keyboard interactions

| Key | Element | Action |
|---|---|---|
| Tab | Remove button | Moves focus to remove button |
| Enter / Space | Remove button | Fires `onRemove(chip.id)` (native `<button>` behaviour) |
| Delete / Backspace | Remove button | **Also fires `onRemove(chip.id)`** — required for accessibility |
| Tab | "Clear all" button | Moves focus to button |
| Enter / Space | "Clear all" button | Fires `onClearAll()` |

### 5.1 Delete / Backspace on chip remove

When keyboard focus is on a chip's remove button (`✕`), pressing `Delete` or `Backspace` MUST also fire `onRemove(chip.id)`. This matches the standard keyboard pattern for removing selected items across web applications. Keyboard users who navigate chip lists with Tab expect Delete/Backspace to remove the focused chip, not just Enter/Space.

Implementation (on each chip's remove `<button>`):
```tsx
<button
  onKeyDown={(e) => {
    if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      onRemove?.(chip.id)
    }
  }}
  onClick={() => onRemove?.(chip.id)}
  ...
/>
```

`e.preventDefault()` on Backspace prevents browser back-navigation.

---

## 6. Hover behaviours

| Element | Hover effect |
|---|---|
| Remove button (`✕`) | `hover:bg-blue-200 hover:text-blue-700` — background highlight |
| "Clear all" button | `hover:text-gray-700 hover:underline` |
| Chip container | No hover effect (chip is not interactive) |

---

## 8. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No animation on chip removal | Chips disappear immediately on re-render; no exit animation |
| I2 | "Clear all" only shows at > 1 chip | Some hosts want it for single-chip too |
| I3 | No optimistic chip removal on click | Component depends entirely on host prop update; briefly shows stale chip if host is async |
| I4 | Delete/Backspace keyboard handler | [RESOLVED 2026-06-06] §5.1: Delete/Backspace now spec'd on chip remove buttons |
