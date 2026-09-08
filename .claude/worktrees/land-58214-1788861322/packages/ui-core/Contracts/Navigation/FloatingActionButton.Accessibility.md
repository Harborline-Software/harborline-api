# FloatingActionButton — Accessibility Contract

- **Component:** FloatingActionButton
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingActionButton.Semantic.md) · [Interaction](./FloatingActionButton.Interaction.md) · [Styling](./FloatingActionButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/FloatingActionButton.tsx`
- **Catalog row:** #60 FloatingActionButton (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | FAB root | Native button — keyboard focusable |
| `aria-label={...}` | FAB root | Compact FAB: explicit `aria-label`, or the string `icon` when no explicit label is supplied |
| `disabled` | FAB root | Boolean attribute when `disabled=true` |

---

## 2. Labeling strategy

| Mode | Label source |
|---|---|
| Compact with string `icon` | Explicit `aria-label` when supplied; otherwise the string `icon` |
| Compact with React-node `icon` | Required explicit `aria-label`; generic fallback labels do not satisfy the contract |
| Extended (`text` present) | Button has visible text label; `aria-label` is `undefined` (text is the accessible name) |

---

## 3. Focus ring

`focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2` — visible at 2px ring. FAB uses `ring-offset-2` (vs offset-1 on Chip) to stand out from the elevated shadow.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FAB1 | Medium | Compact FAB `aria-label` fell back to `"action"` when icon was a React node — not descriptive | **Resolved.** The public API exposes `aria-label` and requires it for compact React-node icons |
