# SearchInput — Accessibility Contract

- **Component:** SearchInput
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchInput.Semantic.md) · [Interaction](./SearchInput.Interaction.md) · [Accessibility](./SearchInput.Accessibility.md) · [Styling](./SearchInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SearchInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

SearchInput is an interactive form control. Its accessibility contract covers: input labelling, role, clear button labelling, search icon decorativity, and keyboard behaviour.

---

## 2. Input element

```html
<input
  type="search"
  role="searchbox"
  aria-label="{placeholder}"
  value={localValue}
  placeholder="{placeholder}"
/>
```

| Attribute | Value | Notes |
|---|---|---|
| `type` | `"search"` | Native search input; browser may add a clear `×` button (varies by browser) |
| `role` | `"searchbox"` | Explicitly declares search semantics; redundant with `type="search"` but safe |
| `aria-label` | `placeholder` prop value | The placeholder string doubles as the accessible name |

SR reads: `"{placeholder}, search, editable text"`.

**Note:** Using `placeholder` as `aria-label` works for SearchInput because the placeholder is always a descriptive action phrase ("Search…", "Search invoices…"). Ensure hosts provide meaningful placeholders.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 1.3.5 Identify Input Purpose.

---

## 3. Search icon

```html
<Search class="pointer-events-none absolute left-3 h-4 w-4 text-gray-400" aria-hidden="true" />
```

Decorative; `aria-hidden`. Correct.

---

## 4. Clear button

```html
<button type="button" aria-label="Clear search">
  <X class="h-3.5 w-3.5" aria-hidden="true" />
</button>
```

| Attribute | Value | Notes |
|---|---|---|
| `aria-label` | `"Clear search"` | Descriptive; icon is decorative |
| `type` | `"button"` | Prevents form submission |

SR reads: `"Clear search, button"`. Correct.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 5. Keyboard

| Key | Behaviour |
|---|---|
| Tab | Focuses the input |
| Type | Updates the search value |
| Tab | Moves to clear button (when visible) |
| Enter / Space | Activates clear button |
| Tab | Moves to next focusable element |

**Gap:** Clear button is in the tab order but visually positioned inside the input. On narrow viewports this may overlap. No fix in reference implementation.

---

## 6. Focus management

SearchInput does not manage focus programmatically. The host focuses the input via `ref` if needed (e.g., Cmd+K shortcut).

---

## 7. Color contrast

The input's placeholder text `text-gray-400` on `bg-white` is approximately 2.7:1. This FAILS WCAG 2.2 SC 1.4.3 for placeholder text (note: placeholder text contrast is NOT a WCAG 2.2 AA requirement — the spec exempts placeholder text from 4.5:1 — but it IS a usability concern).

The actual typed text is `text-gray-900` — high contrast. The border `border-gray-300` on white is approximately 2.4:1 — passes SC 1.4.11 Non-text Contrast (which requires 3:1 for UI components) marginally. Focus state border `focus:ring-blue-500` is high contrast.

**Known gap (A1):** Input border `border-gray-300` is below 3:1 non-text contrast in default state. Use `border-gray-400` or `border-gray-500` for the default state.

---

## 8. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | Default input border contrast below 3:1 (SC 1.4.11) | Low | PAO Styling updates to `border-gray-400` |
| A2 | No `aria-label` on the wrapper div | Low | Acceptable; input has its own `aria-label` |
| A3 | No live region announcing search result count | Low | Host wraps result count in `aria-live="polite"` |
| A4 | No `aria-busy` on wrapper during debounce window | High | Implement on outer div per RA3-13; §3 debounce spec added to Interaction contract |

---

## 9. Live region guidance (host responsibility)

SearchInput itself does not own the results list. The HOST rendering search
results alongside SearchInput is responsible for announcing result count
changes to AT:

```html
<div role="status" aria-live="polite" aria-atomic="true">
  {results.length} results
</div>
```

`role="status"` is equivalent to `aria-live="polite"` + `aria-atomic="true"`.
The host should update this region whenever results change (i.e., after the
debounced `onChange` fires and results are recalculated). Gap A3 (no live
region in reference implementation) remains — this section specifies the
correct pattern for the host.
