# FilterChips — Accessibility Contract

- **Component:** FilterChips
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterChips.Semantic.md) · [Interaction](./FilterChips.Interaction.md) · [Accessibility](./FilterChips.Accessibility.md) · [Styling](./FilterChips.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FilterChips.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FilterChips uses `role="listbox"` / `role="option"` to semantically express a selection list. This contract pins the ARIA structure, its gaps, and the keyboard navigation requirements for a listbox.

---

## 2. ARIA structure

```html
<div role="listbox" aria-multiselectable={multiple}>
  <button role="option" aria-selected={selected}>Label <span>42</span></button>
  …
</div>
```

| Element | Role | Key attributes |
|---|---|---|
| Root `<div>` | `listbox` | `aria-multiselectable={multiple}` |
| Each chip `<button>` | `option` | `aria-selected={selected}` |
| Count `<span>` | (inline text, no role) | — |

**Note:** The WAI-ARIA spec states that `role="option"` should be a child of `role="listbox"`, and options should NOT be `<button>` elements — they should be non-interactive elements in a composite widget with roving tabindex. The reference implementation uses `<button role="option">` which is a known semantic gap (see §6.A1).

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; WAI-ARIA 1.2 Listbox Pattern.

---

## 3. Accessible name

Each chip button's accessible name is its visible text content: `"{label}"` or `"{label} {count}"`. The count is inline text, read by SR as part of the button name.

There is no `aria-label` on the root listbox. Hosts should add `aria-label="Filter by status"` (or similar) to the root when context is not clear from surrounding content.

**Known gap (A2):** Root `role="listbox"` has no `aria-label` or `aria-labelledby`. SR may announce "listbox" without context.

---

## 4. Selection announcement

When a chip is selected, `aria-selected="true"` is set. SR reads: `"{label}, option, selected"`.

When deselected: `aria-selected="false"`. SR reads: `"{label}, option, not selected"`.

This is correct WAI-ARIA semantics.

---

## 5. Keyboard navigation

**Current implementation:** Only Tab navigation is supported — no arrow-key navigation.

**WAI-ARIA requirement for listbox:** The WAI-ARIA Listbox Pattern requires:
- Arrow Up/Down to move focus between options (roving tabindex model).
- Home/End to move to first/last option.
- Tab exits the listbox; focus does NOT move between options via Tab (in the WAI-ARIA model).

The reference implementation does NOT implement the WAI-ARIA Listbox keyboard model — it uses Tab between `<button>` elements, which is the native button tab model, not the listbox model.

**Known gap (A1):** Arrow-key navigation not implemented. See Interaction §6.I1.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 6. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | `<button role="option">` is not WAI-ARIA spec-compliant; listbox needs arrow-key nav with roving tabindex | Medium | Refactor to `<div role="listbox">` with `<div role="option" tabIndex={...}>` and arrow-key handlers |
| A2 | Root listbox has no accessible name | Low | Host adds `aria-label` OR component adds a required/optional `label` prop |
| A3 | Count `<span>` not distinguished from label in SR readout | Low | Add `aria-label="{label}, {count} results"` to each option button |
