# Panel — Accessibility Contract

- **Component:** Panel
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Panel.Semantic.md) · [Interaction](./Panel.Interaction.md) · [Styling](./Panel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Panel.tsx`
- **Catalog row:** #95 Panel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `mode="accordion"` + `resizable`

---

## 1. Static mode

No ARIA attributes added by Panel. All `React.HTMLAttributes` spread means callers can add `aria-label`, `role`, `aria-labelledby`, etc. directly on the Panel element.

---

## 2. Accordion mode

### 2.1 Toggle node button

| Attribute | Element | Value |
|---|---|---|
| `type="button"` | toggle `<button>` | prevents form submission |
| `disabled` | toggle `<button>` | HTML disabled attribute when `item.disabled=true` |
| `aria-expanded` | toggle `<button>` | `"true"` when expanded, `"false"` when collapsed |

Keyboard: native `<button>` behavior — Enter and Space both trigger the click handler.

### 2.2 Leaf node button

| Attribute | Element | Value |
|---|---|---|
| `type="button"` | leaf `<button>` | prevents form submission |
| `disabled` | leaf `<button>` | HTML disabled attribute when `item.disabled=true` |

`aria-expanded` is NOT set on leaf buttons (no children to expand).

### 2.3 Nested items

Child items are indented visually via padding. No `aria-level`, `aria-setsize`, or `aria-posinset` — the tree-view pattern is not used. Items are a flat list of buttons at the visual nesting level.

---

## 3. Resize handles

| Attribute | Element | Value |
|---|---|---|
| `role="separator"` | `ResizeHandle <div>` | Identifies as a resize separator |
| `aria-orientation` | `ResizeHandle <div>` | `"vertical"` for right handle, `"horizontal"` for bottom/corner |
| `aria-label` | `ResizeHandle <div>` | `"Resize handle (right)"`, `"Resize handle (bottom)"`, or `"Resize handle (corner)"` |
| `tabIndex={0}` | `ResizeHandle <div>` | Keyboard focusable |

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PA1 | Low | Accordion items have no `aria-controls` linking toggle button to its child content div | Accepted-risk M1 |
| G-PA2 | Low | No `role="tree"` / `role="treeitem"` for nested item hierarchy — uses flat button pattern | Accepted-risk M1 (tree-view is a separate component concern) |
