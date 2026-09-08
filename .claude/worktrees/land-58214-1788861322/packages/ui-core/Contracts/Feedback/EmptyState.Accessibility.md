# EmptyState — Accessibility Contract

- **Component:** EmptyState
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Interaction](./EmptyState.Interaction.md) · [Styling](./EmptyState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `aria-hidden="true"` | Variant icon | Icons are decorative |

No explicit `role` on the container — EmptyState is a static informational
region; it does not need a live region role as it is rendered once when the
list is empty, not dynamically injected.

---

## 2. Keyboard behavior

| Element | Keyboard |
| --- | --- |
| Action button | `Tab` to focus; `Space`/`Enter` to activate |

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-ES1 | Low | No `role="status"` or live region — when a data list transitions from loaded to empty (e.g., after a delete), AT may not announce the empty state | Accepted-risk M1 |
| G-ES2 | Low | Title and description use `<p>` elements rather than heading/paragraph hierarchy — suitable for a datagrid inset but may lack structure in standalone contexts | Accepted-risk M1 |
