# EmptyState — Interaction Contract

- **Component:** EmptyState
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Accessibility](./EmptyState.Accessibility.md) · [Styling](./EmptyState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Action button

| Condition | Trigger | Effect |
| --- | --- | --- |
| `action` provided | Button click | `action.onClick()` |
| `action` absent | — | No button rendered |

The action button is a `<button type="button">`. All navigation or record
creation logic is the host's responsibility.

---

## 2. Display-only (no action)

When `action` is absent, EmptyState is purely display-only — no user
interaction beyond any action button.

---

## 3. Known gaps

None. Interaction surface is intentionally minimal.
