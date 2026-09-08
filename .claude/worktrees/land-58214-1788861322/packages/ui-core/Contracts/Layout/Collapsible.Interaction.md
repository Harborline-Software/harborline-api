# Collapsible — Interaction Contract

- **Component:** Collapsible
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Collapsible.Semantic.md) · [Accessibility](./Collapsible.Accessibility.md) · [Styling](./Collapsible.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Collapsible.tsx`
- **Catalog row:** #A17 Collapsible (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `preset="panel"`

---

## 1. Headless mode

### 1.1 Toggle

`CollapsibleTrigger` click toggles `open` state (or fires `onOpenChange` in controlled mode).

### 1.2 Disabled

When `disabled=true` on the root, `CollapsibleTrigger` is disabled and cannot toggle.

### 1.3 Keyboard behavior

`CollapsibleTrigger` is a `<button>` — Enter and Space toggle the state. Native button keyboard behavior applies.

### 1.4 Animation

Content transition is CSS-based — `CollapsibleContent` animates height on open/close. Exact animation is caller-controlled via `className`.

---

## 2. Panel preset mode (`preset="panel"`)

### 2.1 Toggle

`toggle()` is triggered by `onClick` on the header `<button>`.

`toggle()`:
1. If `disabled`: returns early.
2. Computes `next = !isOpen`.
3. Updates internal state (uncontrolled) or fires `onOpenChange(next)` (controlled).

### 2.2 Keyboard

Header is a native `<button>` — Enter and Space fire the click handler automatically.

`disabled` prop sets HTML `disabled` on the `<button>` — keyboard unreachable when disabled.

### 2.3 headerActions stopPropagation

Clicks inside `headerActions` call `e.stopPropagation()` — they do NOT propagate to the header `onClick` and therefore do NOT toggle the panel.

---

## 3. Known gaps

None.
