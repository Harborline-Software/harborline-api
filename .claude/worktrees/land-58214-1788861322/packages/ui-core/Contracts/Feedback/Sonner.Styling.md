# Sonner — Styling Contract

- **Component:** Sonner / ToastQueue
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sonner.Semantic.md) · [Interaction](./Sonner.Interaction.md) · [Accessibility](./Sonner.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A6 Sonner / ToastQueue (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; sonner + shadcn Sonner baseline)

---

## 1. Toaster container

`fixed z-[100]` + position-based placement (e.g., `bottom-0 right-0` for `bottom-right`).

---

## 2. Individual toast

Base: `group pointer-events-auto relative flex w-full items-center justify-between space-x-4 overflow-hidden rounded-md border border-border p-6 pr-8 shadow-lg transition-all`

Width: `max-w-[420px]`

---

## 3. Toast variants (rich colors)

When `richColors=true`:

| Type | Background | Border | Text |
|---|---|---|---|
| default | `bg-background text-foreground` | `border-border` | |
| success | `bg-green-50 text-green-900` | `border-green-200` | |
| error | `bg-red-50 text-red-900` | `border-red-200` | |
| warning | `bg-yellow-50 text-yellow-900` | `border-yellow-200` | |
| info | `bg-blue-50 text-blue-900` | `border-blue-200` | |

When `richColors=false` (default): all toasts use `bg-background text-foreground border-border`.

---

## 4. Stack appearance

3D stacking effect via CSS `translateY` and `scale` transforms on older toasts in the stack — managed by sonner internally.

---

## 5. Animation

Slide in from the position edge; fade out on dismiss. `data-[swipe=move]` applies transform during swipe.

---

## 6. Design tokens

Default mode uses design tokens: `bg-background`, `text-foreground`, `border-border`. Rich-color variants use hardcoded palette values — M2 will tokenize.
