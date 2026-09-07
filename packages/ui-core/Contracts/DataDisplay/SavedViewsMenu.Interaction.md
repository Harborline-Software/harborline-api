# SavedViewsMenu — Interaction Contract

- **Component:** SavedViewsMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SavedViewsMenu.Semantic.md) · [Interaction](./SavedViewsMenu.Interaction.md) · [Accessibility](./SavedViewsMenu.Accessibility.md) · [Styling](./SavedViewsMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SavedViewsMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
[closed]
  │ trigger click → [open, views refreshed from store]
  │
[open]
  ├── name input → updates nameInput
  ├── Enter in input / Save button click
  │     ├── name is empty → no-op
  │     └── name is trimmed → store.save() → views refreshed → nameInput cleared → stay open
  ├── apply view click → onApply(view) → popover stay open (Radix)
  ├── delete view click → e.stopPropagation → store.remove() → views refreshed → stay open
  ├── Escape → [closed, focus returns to trigger]
  └── click outside → [closed]
```

---

## 2. Save behaviour

| Condition | Result |
|---|---|
| `nameInput.trim()` is empty | `handleSave` returns early; nothing saved; Save button is `disabled` |
| `nameInput.trim()` is non-empty | `store.save({ name, ...currentState })` called; `views` refreshed; `nameInput` cleared |

The Save button carries `disabled={!nameInput.trim()}`.

---

## 3. Apply behaviour

Clicking a saved view name button calls `onApply(view)`. The popover does NOT close on apply — this is a minor UX gap (see §5.I1).

---

## 4. Delete behaviour

The delete button calls `e.stopPropagation()` to prevent the parent apply button from firing. The `store.remove(id)` call updates the store; `views` is refreshed from the store.

The popover stays open after delete.

---

## 5. Keyboard behaviour

| Key | Element | Action |
|---|---|---|
| Enter | Name input | Fires `handleSave` (if non-empty) |
| Enter / Space | Trigger button | Toggles popover |
| Enter / Space | Save button | Fires `handleSave` |
| Enter / Space | View name button | Fires `onApply(view)` |
| Enter / Space | Delete button | Fires `handleDelete` |
| Tab / Shift+Tab | Within popover | Moves between interactive elements |
| Escape | Anywhere in popover | Closes popover; focus returns to trigger |

---

## 6. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | Popover does not auto-close after applying a view | User must dismiss manually; minor UX friction |
| I2 | No confirmation on delete | Views are deleted immediately without undo |
| I3 | No duplicate-name prevention | `store.save` accepts duplicate names; list shows two entries with the same name |
| I4 | Enter on Save button when disabled still fires `handleSave` (returns early, no-op) | Acceptable but could fire a validation message |
