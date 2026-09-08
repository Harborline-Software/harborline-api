# Collapsible — Accessibility Contract

- **Component:** Collapsible
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Collapsible.Semantic.md) · [Interaction](./Collapsible.Interaction.md) · [Styling](./Collapsible.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Collapsible.tsx`
- **Catalog row:** #A17 Collapsible (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `preset="panel"`

---

## 1. Headless mode

### 1.1 Trigger

`CollapsibleTrigger` renders with:
- `aria-expanded={open}` — open/closed state.
- `aria-controls="{contentId}"` — links to the `CollapsibleContent` element id.

### 1.2 Content visibility

`CollapsibleContent` uses `data-state="closed"` when closed. Content is conditionally rendered (unmounted when closed) so AT does not encounter hidden content.

### 1.3 Keyboard

Native button semantics on `CollapsibleTrigger` — Enter and Space work natively for toggle.

---

## 2. Panel preset mode (`preset="panel"`)

### 2.1 Header button

| Attribute | Element | Value |
|---|---|---|
| `type="button"` | header `<button>` | prevents form submission |
| `disabled` | header `<button>` | HTML disabled attribute when `disabled=true` |
| `aria-expanded` | header `<button>` | `"true"` when open, `"false"` when closed |
| `aria-controls` | header `<button>` | id of the content region element |
| `id` | header `<button>` | stable id for `aria-labelledby` on content |

### 2.2 Content region

| Attribute | Element | Value |
|---|---|---|
| `id` | content `<div>` | matches `aria-controls` on header button |
| `role="region"` | content `<div>` | landmark for AT navigation |
| `aria-labelledby` | content `<div>` | points to header button id |

### 2.3 Chevron icon

Chevron `▾` is wrapped in a `<span>` that is `aria-hidden` to prevent AT from reading the indicator character.

---

## 3. Known gaps

None.
