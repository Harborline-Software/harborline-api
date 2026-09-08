# Popover — Interaction Contract

- **Component:** Popover
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popover.Semantic.md) · [Accessibility](./Popover.Accessibility.md) · [Styling](./Popover.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Popover.tsx`
- **Catalog row:** #98 Popover (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

All interaction is delegated to `@radix-ui/react-popover`. The Harborline wrapper adds no custom interaction logic.

Radix defaults:
- `PopoverTrigger` click → open/close toggle
- Click outside → close
- `Escape` → close
- `PopoverClose` click → close

---

## 2. Focus management (Radix behavior)

- On open: focus moves to the first focusable element inside `PopoverContent`
- On close: focus returns to `PopoverTrigger`
- Focus is trapped within the popover while open

---

## 3. Known gaps

None. Interaction is fully provided by Radix UI.
