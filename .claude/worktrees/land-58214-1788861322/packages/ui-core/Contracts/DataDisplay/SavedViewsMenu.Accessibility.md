# SavedViewsMenu — Accessibility Contract

- **Component:** SavedViewsMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SavedViewsMenu.Semantic.md) · [Interaction](./SavedViewsMenu.Interaction.md) · [Accessibility](./SavedViewsMenu.Accessibility.md) · [Styling](./SavedViewsMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SavedViewsMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

SavedViewsMenu has an interactive popover with a text input, a save button, a list of named views with apply buttons, and delete buttons. This contract covers trigger button labelling, popover structure, form labelling, and focus management.

---

## 2. Trigger button

| Attribute | Value |
|---|---|
| `aria-label` | `"Saved views"` |
| `aria-haspopup` | `"menu"` (currently set in source) |
| `aria-expanded` | Injected by Radix via `asChild` (verify in rendered output) |

SR reads: `"Saved views, button, collapsed"` / `"Saved views, button, expanded"`.

**Note:** The trigger carries `aria-haspopup="menu"` but the popover content is more of a dialog/panel (contains a form + list) than a menu. `aria-haspopup="dialog"` would be more accurate.

**Known gap (A1):** `aria-haspopup="menu"` is semantically imprecise for a popover containing a form and a list. Consider `aria-haspopup="dialog"`.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Save view form

The save form section has a section label: `<p>Save current view</p>` (decorative, uppercase, small). This is NOT an associated `<label>` for the input.

**Known gap (A2):** The name input has no `<label>` element or `aria-label`. SR announces the input without a name. Add `aria-label="View name"` or `<label>`.

The Save button has visible text "Save" — accessible name is correct.

---

## 4. Saved views list

Each saved view has an apply button (the view name text) and a delete button.

| Element | Accessible name |
|---|---|
| Apply button | View name text (e.g., `"Q1 Revenue"`) |
| Delete button | `aria-label="Delete view Q1 Revenue"` |

SR reads: `"Q1 Revenue, button"` for apply; `"Delete view Q1 Revenue, button"` for delete.

The delete `aria-label` is correct and specific. No gap here.

---

## 5. Empty state

When `views.length === 0`:

```html
<p>No saved views yet.</p>
```

SR reads the paragraph text. No `role` needed — it's informational text.

---

## 6. Keyboard navigation

Fully handled by Radix Popover. See Interaction contract §5 for key map.

Focus management:
- **Open:** Radix moves focus to first interactive element (name input) on popover open.
- **Close:** Radix returns focus to the trigger button on Escape.
- **Tab cycle:** Moves through: name input → Save button → view 1 apply → view 1 delete → view 2 apply → view 2 delete → … → loops.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; SC 2.4.3 Focus Order.

---

## 7. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | `aria-haspopup="menu"` imprecise | Low | Change to `aria-haspopup="dialog"` |
| A2 | Name input has no accessible label | High | Add `aria-label="View name"` to the input |
| A3 | "Save current view" section header is not a heading | Low | Acceptable as visual label; AT users navigate by tab, not by heading, within a popover |
| A4 | No announcement when view is saved or deleted | Low | Wrap save/delete confirmation in `aria-live="polite"` region |
