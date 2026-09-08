# FilterBar — Accessibility Contract

- **Component:** FilterBar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterBar.Semantic.md) · [Interaction](./FilterBar.Interaction.md) · [Accessibility](./FilterBar.Accessibility.md) · [Styling](./FilterBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/FilterBar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FilterBar uses a `role="list"` / `role="listitem"` structure to expose active filters to AT. Each chip has an accessible remove button with a descriptive `aria-label`. This contract pins the ARIA structure and keyboard behaviour.

---

## 2. ARIA structure

When chips are present:

```html
<div role="list" aria-label="Active filters">
  <span role="listitem">
    <span>Label</span>
    <span>: value</span>
    <button aria-label="Remove Label filter">✕</button>
  </span>
  …
</div>
```

| Element | Role | Accessible name |
|---|---|---|
| Root `<div>` | `list` | `"Active filters"` (via `aria-label`) |
| Each chip `<span>` | `listitem` | — (text content read as part of list) |
| Remove button | `button` | `"Remove {chip.label} filter"` |
| "Clear all" button | `button` | `"Clear all"` (visible text) |
| `⊘` placeholder icon | decorative | `aria-hidden="true"` |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 1.3.1 Info and Relationships.

---

## 3. Empty state

When `chips.length === 0` and `placeholder` is truthy:

```html
<div class="...text-gray-400">
  <span aria-hidden="true">⊘</span>
  No active filters
</div>
```

The `⊘` icon is `aria-hidden`. SR reads the placeholder text. The container has no `role` — it is a non-interactive informational note.

---

## 4. Keyboard navigation

| Key | Element | Action |
|---|---|---|
| Tab / Shift+Tab | Remove buttons, "Clear all" | Moves focus through interactive elements in DOM order |
| Enter / Space | Remove button | Fires `onRemove(id)` |
| Enter / Space | "Clear all" | Fires `onClearAll()` |

Non-interactive chip text is NOT in the tab order.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; SC 2.4.3 Focus Order.

---

## 5. Remove button touch target

The remove button has `p-0.5` padding on a `✕` character. Total target size is approximately 18–20px. This is below the WCAG 2.2 SC 2.5.8 minimum of 24×24px.

**Known gap (A1):** Remove button touch target is too small. PAO Styling should increase to `p-1.5` or wrap in a larger hit area.

---

## 6. Color contrast

The chip uses `text-blue-700` on `bg-blue-50`. Contrast ratio is approximately 5.5:1 — passes WCAG 2.2 SC 1.4.3 (minimum 4.5:1 for normal text).

The remove button icon `text-blue-400` on `bg-blue-50` is approximately 2.8:1 — below 3:1 for non-text contrast (WCAG 2.2 SC 1.4.11).

**Known gap (A2):** Remove button icon colour `text-blue-400` fails non-text contrast. Use `text-blue-600` minimum.

---

## 7. Known gaps

| # | Item | Resolution path |
|---|---|---|
| A1 | Remove button touch target below 24×24px | PAO Styling increases padding |
| A2 | Remove button icon `text-blue-400` fails 3:1 non-text contrast | PAO Styling updates to `text-blue-600` |
| A3 | No `aria-live` announcement when chips change | Hosts adding/removing filters produce no AT announcement; hosts may wrap in `aria-live="polite"` region |
