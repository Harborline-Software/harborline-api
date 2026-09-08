# Sonner — Interaction Contract

- **Component:** Sonner / ToastQueue
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sonner.Semantic.md) · [Accessibility](./Sonner.Accessibility.md) · [Styling](./Sonner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A6 Sonner / ToastQueue (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; sonner + shadcn Sonner baseline)

---

## 1. Auto-dismiss

Toasts auto-dismiss after `duration` ms. The timer pauses when the user hovers over the toast queue (pointer enters the Toaster region).

---

## 2. Manual dismiss

- Swipe gesture (touch): dismisses toast.
- Close button click (when `closeButton=true`): dismisses toast.
- `toast.dismiss(id)`: programmatic dismiss.

---

## 3. Expand / collapse

When `expand=false` (default): toasts stack in a 3D layered view — only the topmost toast is fully visible; older toasts peek from behind.

When `expand=true`: all `visibleToasts` toasts are fully expanded and visible.

Hovering over the stack expands it (shows all visible toasts) in default mode.

---

## 4. Queue management

When more than `visibleToasts` toasts are queued, older toasts are hidden until newer ones dismiss.

---

## 5. Promise toasts

`toast.promise()` transitions a single toast through loading → success/error states when the promise resolves or rejects.

---

## 6. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
