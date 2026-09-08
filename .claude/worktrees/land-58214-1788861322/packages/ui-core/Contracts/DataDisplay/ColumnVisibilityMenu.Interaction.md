# ColumnVisibilityMenu — Interaction Contract

- **Component:** ColumnVisibilityMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColumnVisibilityMenu.Semantic.md) · [Interaction](./ColumnVisibilityMenu.Interaction.md) · [Accessibility](./ColumnVisibilityMenu.Accessibility.md) · [Styling](./ColumnVisibilityMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ColumnVisibilityMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

ColumnVisibilityMenu has one internal state axis: the popover open/closed state (owned by Radix Popover).

```
[closed] ──trigger click──> [open]
[open]   ──Escape──────────> [closed]
[open]   ──click outside───> [closed]
[open]   ──checkbox toggle──> [open]  + onChange fired
```

Column visibility state is **not** internal — it is fully controlled by `visibility` / `onChange` props.

---

## 2. Trigger button behaviour

| Interaction | Result |
|---|---|
| Click trigger button | Opens popover (Radix manages) |
| Click trigger when open | Closes popover |
| Keyboard Enter/Space on trigger | Opens/closes popover |

The trigger button displays the `hiddenCount` badge (`>0` when any non-required columns are hidden). The badge count is live — it updates immediately on each `onChange`.

---

## 3. Checkbox toggle behaviour

Each column row in the popover contains a Radix Checkbox.

| Interaction | Result |
|---|---|
| Click row or checkbox | Toggles visibility; `onChange(next)` fires immediately |
| Click required column row | No effect (checkbox is `disabled`) |
| Click unchecked → checked | Column becomes visible; `onChange` called with `{...visibility, [id]: true}` |
| Click checked → unchecked | Column hidden; `onChange` called with `{...visibility, [id]: false}` |

The popover stays **open** after each checkbox toggle — the user may adjust multiple columns in one open cycle.

---

## 4. Popover dismissal

Handled entirely by Radix Popover:

| Trigger | Result |
|---|---|
| Escape key | Closes popover; focus returns to trigger button |
| Click outside popover | Closes popover |
| Tab away from popover content | Closes popover (Radix default) |

---

## 5. Keyboard interactions

| Key | Focused element | Action |
|---|---|---|
| Tab | Trigger button | Opens popover focus (Radix manages focus entry) |
| Enter / Space | Trigger button | Toggles popover open/closed |
| Space | Checkbox | Toggles column visibility |
| Tab / Shift+Tab | Within popover | Moves between checkboxes |
| Escape | Anywhere in popover | Closes popover |

---

## 6. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No `onOpen` / `onClose` callback | Hosts that need to react to popover state changes must use a Radix `onOpenChange` workaround at the parent level — the component does not expose it |
| I2 | No bulk-toggle affordance | "Show all" / "Hide all" not implemented |
| I3 | Required columns show disabled checkbox but row is still visually clickable | Row `cursor-pointer` class on the `<div>` wrapper is misleading for required columns; clicking the row has no effect |
