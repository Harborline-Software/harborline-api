# Sonner — Semantic Contract

- **Component:** Sonner / ToastQueue
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Sonner.Interaction.md) · [Accessibility](./Sonner.Accessibility.md) · [Styling](./Sonner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: shadcn Sonner / sonner npm package)
- **Catalog row:** #A6 Sonner / ToastQueue (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; sonner + shadcn Sonner baseline)

---

## 1. Component purpose

**Sonner** (alias: ToastQueue) — a stacked toast notification manager. Renders multiple toast notifications in a fixed viewport position, stacking them with a 3D layered appearance. Provides a programmatic `toast()` imperative API in addition to the declarative `<Toaster>` component.

**Distinct from `Notification` (#89) — OO-7 disambiguation:** Both Notification and Sonner manage a toast queue with FIFO eviction and a `max` prop. Key differences: (1) **Library**: Sonner wraps the `sonner` npm package's API; Notification is in-house with a Radix Toast fallback. (2) **Scope**: Notification is `v1` (available now); Sonner is `planned` (not in v1). (3) **Appearance**: Sonner renders a 3D-stacked tray from the `sonner` library; Notification uses Harborline token surface. Use Notification for v1 production use. Use Sonner when you specifically need the `sonner` library's visual and API. See [Notification.Semantic.md](./Notification.Semantic.md) for the canonical v1 toast system.

---

## 2. Toaster component (planned)

```typescript
interface ToasterProps {
  theme?: 'light' | 'dark' | 'system'
  position?: 'top-left' | 'top-center' | 'top-right' | 'bottom-left' | 'bottom-center' | 'bottom-right'
  // default: 'bottom-right'
  richColors?: boolean        // default: false
  expand?: boolean            // default: false — stacked (collapsed) vs expanded mode
  duration?: number           // default toast duration ms; default: 4000
  visibleToasts?: number      // max visible; default: 3
  closeButton?: boolean       // default: false
  toastOptions?: ToastOptions // per-type overrides
}
```

---

## 3. Imperative `toast()` API

```typescript
toast(message: string, options?: ToastOptions): string | number  // returns toast ID
toast.success(message, options?)
toast.error(message, options?)
toast.warning(message, options?)
toast.info(message, options?)
toast.loading(message, options?)
toast.promise(promise, { loading, success, error })
toast.dismiss(id?)   // dismiss specific or all
```

---

## 4. Toast lifecycle

A toast appears, auto-dismisses after `duration` ms, and can be manually dismissed. Loading toasts persist until `toast.dismiss()` or the promise resolves.

### 4.1 WCAG 2.2.1 — error toasts MUST NOT auto-dismiss

**`toast.error()` calls MUST NOT respect the global `duration` setting.**
Error toasts persist until the user explicitly dismisses them or `toast.dismiss(id)` is called.

WCAG 2.2.1 Timing Adjustable (Level A) applies to auto-dismissing content. Error messages
do not meet any WCAG 2.2.1 exception (real-time events, essential timing, 20-hour minimum).
Allowing error toasts to auto-dismiss in 4 s is a guaranteed Level A failure.

**Implementation rule:** When building the Harborline wrapper around Sonner, the internal adapter
MUST override `duration` to `Infinity` for `toast.error()` calls and expose a `<Toaster
forceErrorPersistent={boolean}>` prop (default `true`) as the only opt-out.

```typescript
// Correct — error toast is persistent
toast.error('Failed to save')                        // duration → Infinity
toast.error('Failed to save', { duration: 4000 })   // duration 4000 IGNORED → Infinity

// Only valid exception (requires host-provided accessibility workaround):
// <Toaster forceErrorPersistent={false}>
```

This mirrors the `forceErrorPersistent` contract in `Notification.Semantic.md §3.5.2`.
