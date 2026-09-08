# ActionSheet — Interaction Contract

- **Component:** ActionSheet
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionSheet.Semantic.md) · [Accessibility](./ActionSheet.Accessibility.md) · [Styling](./ActionSheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ActionSheet.tsx`
- **Catalog row:** #1 ActionSheet (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED (open=false)
  → setOpen(true) (external trigger) → OPEN

OPEN (open=true)
  → click item (enabled) → item.onClick?.() → CLOSED
  → click item (disabled) → no-op
  → click cancel button → onCancel?.() → CLOSED
  → click backdrop → onCancel?.() → CLOSED
```

---

## 2. Backdrop click

The backdrop `<div>` covers the full viewport at z-40. A click on it calls `handleCancel`, which fires `onCancel()` and `setOpen(false)`. Backdrop has `aria-hidden="true"`.

---

## 3. No keyboard-close in M1

The ActionSheet does not implement Escape key handling. Modal closure is pointer-only in M1.

---

## 4. Disabled items

Disabled items have the `disabled` HTML attribute and `onClick` is suppressed in `handleItem`. They render with reduced opacity but are still shown.

---

## 5. Rendering

`if (!isOpen) return null` — the sheet is unmounted when closed, not hidden. No animation reverse (slide-out); the component simply disappears.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AS1 | High | Escape key does not close the sheet | Accepted-risk M1; mobile touch pattern; pointer-close is primary |
| G-AS2 | Medium | No focus trap — keyboard users can Tab outside the sheet | Accepted-risk M1 |
| G-AS3 | Low | No slide-out exit animation — sheet unmounts immediately on close | Accepted-risk M1 |
