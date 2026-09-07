# Sonner — Accessibility Contract

- **Component:** Sonner / ToastQueue
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sonner.Semantic.md) · [Interaction](./Sonner.Interaction.md) · [Styling](./Sonner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Sonner.tsx`
- **Catalog row:** #A6 Sonner / ToastQueue (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; sonner + shadcn Sonner baseline)

---

## 1. Live region

Toasts render inside a generic viewport container. The live-region role is per-toast — individual
toasts carry the role, rather than inheriting list semantics from the viewport. Screen readers
announce new toasts as they appear.

---

## 2. Role per intent

| Intent | Role | `aria-live` | Notes |
|---|---|---|---|
| `success` | `status` | implicit `polite` (DO NOT add explicit aria-live) | Non-urgent; SR announces when it finishes current speech |
| `info` | `status` | implicit `polite` | Non-urgent informational |
| `warning` | `status` | implicit `polite` | Non-urgent caution |
| `error` | `alert` | implicit `assertive` (DO NOT add explicit aria-live) | Urgent; SR interrupts immediately |

`role="status"` and `role="alert"` carry IMPLICIT `aria-live` values — adding an explicit `aria-live` attribute alongside them creates a double-announcement in some screen readers. **Forbidden: `<div role="alert" aria-live="assertive">`.**

---

## 3. WCAG 2.2.1 — error toasts MUST NOT auto-dismiss

Error toasts carry actionable information users must have time to read and act on. WCAG 2.2.1 Timing Adjustable (Level A) prohibits time-limiting this content — no WCAG 2.2.1 exception applies to toast error messages.

**Error toasts MUST persist until the user dismisses them.** The Harborline Sonner adapter MUST override any `duration` passed to `toast.error()` and clamp it to `Infinity`. See `Sonner.Semantic.md §4.1` for the implementation contract.

Non-error variants (success/info/warning) are subject to normal auto-dismiss. WCAG 2.2.1 is satisfied for those variants by the hover-pause behaviour (user hovering = reading; timer pauses).

**WCAG citation:** WCAG 2.2 SC 2.2.1 Timing Adjustable.

---

## 4. Focus management

Toasts do not steal focus on appear (would violate SC 3.2.1 On Focus). Interactive toasts (action buttons, close button) are keyboard-reachable via Tab in DOM order — the toaster region appends to `<body>` so Tab may reach it after all page content.

---

## 5. Close button

When `closeButton=true`, each toast has an accessible close button:
- `aria-label="Close"` or `aria-label="Dismiss notification"` (i18n-localised)
- Touch target ≥ 24×24 CSS pixels (WCAG 2.5.8)
- `type="button"` (not submit)

For error toasts (which MUST NOT auto-dismiss), a close button is REQUIRED (not optional). The user needs a keyboard path to dismiss the persistent error toast.

---

## 6. Reduced motion

Sonner respects `prefers-reduced-motion: reduce` — disables slide/stack animations. The wrapper MUST not override this. **WCAG citation:** SC 2.3.3 Animation from Interactions.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SON-A1 | Medium | Stacked/collapsed view hides older toasts — AT may miss them if they auto-dismiss before stack is explored | Accepted-risk; `expand=true` mitigates for AT-sensitive contexts |
| G-SON-A2 | Critical | Error toasts defaulted to auto-dismiss (inherited `duration: 4000`) — WCAG 2.2.1 Level A violation | [RESOLVED 2026-06-06] §3 + Sonner.Semantic §4.1: `forceErrorPersistent` adapter constraint documented |
