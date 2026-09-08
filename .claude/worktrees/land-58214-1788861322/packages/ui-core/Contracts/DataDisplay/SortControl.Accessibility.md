# SortControl — Accessibility Contract

- **Component:** SortControl
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SortControl.Semantic.md) · [Interaction](./SortControl.Interaction.md) · [Accessibility](./SortControl.Accessibility.md) · [Styling](./SortControl.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SortControl.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

SortControl is an interactive control group. Its accessibility contract covers: group labelling, select labelling, toggle button naming, direction state announcement, and keyboard behaviour.

---

## 2. Group wrapper

```html
<div role="group" aria-label="Sort">
```

| Attribute | Value | Notes |
|---|---|---|
| `role` | `"group"` | Groups the select and toggle as a logical unit |
| `aria-label` | `"Sort"` | Names the group for AT |

SR reads: `"Sort, group"` when entering the group.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

---

## 3. Sort field select

```html
<select aria-label="Sort by">…</select>
```

| Attribute | Value | Notes |
|---|---|---|
| `aria-label` | `"Sort by"` | Names the select |

SR reads: `"Sort by, combobox"` followed by the current option value.

Each `<option>` has visible text as its accessible name. No additional ARIA needed on options.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Direction toggle button

```html
<button
  aria-label="Sort"
>
  <ArrowUpDown aria-hidden />
</button>
```

| Attribute | Value | Notes |
|---|---|---|
| `aria-label` | `"Sort"` when no sort is active; otherwise dynamic: `"Sort ascending, click to toggle"` or `"Sort descending, click to toggle"` | The null state names the control without asserting a binary state |
| `aria-pressed` | Omitted when no sort is active; otherwise `currentDir === 'desc'` | `true` when an active sort is descending |

SR reads: `"Sort, button"` when no sort is active, or `"Sort ascending, click to toggle, button, not pressed"` / `"Sort descending, click to toggle, button, pressed"` for an active sort.

**Note:** The active-state `aria-label` reflects the CURRENT direction, not the direction it WILL change to. The null-state exception, shipped in PR 2698, intentionally avoids `aria-pressed="false"`: the control has no active binary sort state.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; WAI-ARIA `aria-pressed` pattern.

---

## 5. Icon accessibility

All three icons (`ArrowUpDown`, `ArrowUp`, `ArrowDown`) carry `aria-hidden="true"` implicitly (Lucide React components add it by default). The button's `aria-label` provides the accessible name.

---

## 6. Keyboard

| Key | Element | Action |
|---|---|---|
| Tab | Select | Focuses |
| Arrow keys | Select | Moves between options (native) |
| Tab | Toggle button | Focuses |
| Enter / Space | Toggle button | Toggles direction |

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 7. Known gaps

| # | Item | Notes |
|---|---|---|
| A1 | No `aria-live` announcement of sort change | Hosts should wrap result list in `aria-live` to announce sort changes |
| A2 | `aria-pressed` is boolean but direction has 3 states (null/asc/desc) | Closed 2026-07-17 by PRs 2698/2742: the null state omits `aria-pressed` and uses the neutral `Sort` label. |
