# Notification — Semantic Contract

- **Component:** Notification (Toast)
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Notification.Interaction.md) · [Styling](./Notification.Styling.md) · [Accessibility](./Notification.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Notification.tsx`
- **Catalog row:** #89 Notification / Toast (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Toast / shadcn Sonner`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** shadcn Sonner (preferred) — wrapping `sonner` library; alternative path is Radix UI `@radix-ui/react-toast`

---

## 1. Purpose

Notification is the canonical **transient, non-blocking system-feedback toast**
of `@harborline-software/ui-react`. It announces brief status updates ("Invoice saved",
"Failed to load — retry"), confirms user actions, and surfaces non-urgent
errors. Notifications appear in a corner of the viewport, persist for a
short duration, and dismiss automatically (or on user action).

This is a **forward-spec**: Notification has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the shadcn/Sonner pattern (provider + imperative API), adapted to
Harborline's variant vocabulary.

Notification differs from sibling Feedback components:

- **StatusBanner** (M1) — full-width inline banner; persistent, in-flow.
- **Notification** (this) — corner-pinned toast; transient, overlay.
- **EmptyState** (M1) — page-region placeholder; persistent, in-flow.
- **Sonner** (#A6, `planned`) — alternative toast-queue built on the `sonner` npm library with stacked-3D appearance and `toast()` imperative API from the library. Use Notification for v1 production use; Sonner is `library-scope: planned` (not available in v1). Both manage queues; key differences: Notification is in-house with Radix Toast fallback path; Sonner wraps an external library. See [Sonner.Semantic.md](./Sonner.Semantic.md) for details. **(OO-7 disambiguation)**

The component family has two parts:

1. **`<NotificationProvider>`** — wraps the application; renders the
   notification viewport (the actual toast container).
2. **`useNotification()`** + imperative `notify()` API — host code calls
   `notify.success("Saved")` etc. to emit a toast. No JSX per-toast.

The imperative API is the shadcn/Sonner pattern. Hosts do **not** render
`<Notification>` JSX themselves; they call the imperative API.

---

## 2. Data model

```typescript
type NotificationVariant =
  | 'info'
  | 'success'
  | 'warning'
  | 'error'

type NotificationPosition =
  | 'top-left' | 'top-center' | 'top-right'
  | 'bottom-left' | 'bottom-center' | 'bottom-right'

interface NotificationAction {
  label: string
  onClick: () => void
}

interface NotificationOptions {
  id?: string                      // Stable identity; reusing dedupes
  variant?: NotificationVariant
  title?: string
  description?: string
  duration?: number                // ms; 0 means persistent (until dismissed)
  action?: NotificationAction      // Optional inline action button
  onDismiss?: () => void           // Fires when the toast dismisses (auto or manual)
}

interface NotificationApi {
  notify: (opts: NotificationOptions) => string  // returns the toast's id
  success: (titleOrOpts: string | NotificationOptions) => string
  info: (titleOrOpts: string | NotificationOptions) => string
  warning: (titleOrOpts: string | NotificationOptions) => string
  error: (titleOrOpts: string | NotificationOptions) => string
  dismiss: (id?: string) => void   // omit id → dismiss all
}

interface NotificationProviderProps {
  position?: NotificationPosition
  defaultDuration?: number
  max?: number
  /**
   * When true (default), error-variant toasts are NEVER subject to auto-dismiss
   * regardless of the per-toast `duration` or `defaultDuration` setting.
   * WCAG 2.2.1 Timing Adjustable — error messages must not be time-limited.
   * Set to false ONLY if a host explicitly needs a dismissing error toast AND
   * provides an alternative accessibility mechanism.
   */
  forceErrorPersistent?: boolean  // default: true
  children: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

### 3.1 `<NotificationProvider>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | `NotificationPosition` | `'bottom-right'` | Viewport corner the toasts appear in. New toasts enter from the closest viewport edge. |
| `defaultDuration` | `number` | `4000` (ms) | Auto-dismiss timeout for toasts that don't specify their own `duration`. |
| `max` | `number` | `5` | Maximum visible toasts. When exceeded, the oldest is dismissed (FIFO). |
| `children` | `ReactNode` | _required_ | App tree. Provider must wrap the consumers of `useNotification()`. |

### 3.2 `NotificationOptions` (per-toast)

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | `string` | auto-generated | Stable identity. Calling `notify({ id: 'save' })` twice with the same `id` **dedupes** — the second call updates the existing toast rather than stacking a new one. |
| `variant` | `'info' \| 'success' \| 'warning' \| 'error'` | `'info'` | Mood / intent. Drives the leading icon and (per PAO Styling) the colour family. |
| `title` | `string` | — | Primary message. Either `title` or `description` (or both) is required at runtime; an empty toast is a host error. |
| `description` | `string` | — | Optional secondary line. |
| `duration` | `number` | provider's `defaultDuration` | Auto-dismiss timeout in ms. `0` means persistent (toast stays until dismissed manually or via `dismiss(id)`). |
| `action` | `NotificationAction` | — | Optional inline action button — `{ label, onClick }`. Clicking the action fires `onClick` **then auto-dismisses the toast**. Hosts whose action may fail (e.g. "Retry") must re-emit a new toast if the retry fails — the dismissed toast cannot be un-dismissed. This is a deliberate UX decision: most "Retry / Undo / View" actions expect dismissal on activation. **Closes audit gap G-NF2.** |
| `onDismiss` | `() => void` | — | Fires when the toast dismisses (any reason — auto-timeout, manual close, action click, dedupe replacement). |

### 3.3 Variant semantics

| Variant | Icon (canonical) | Typical use |
| --- | --- | --- |
| `info` | `Info` (lucide) | Neutral status update. "Property updated", "Sync complete". |
| `success` | `CheckCircle2` (lucide) | Positive confirmation of a user action. "Invoice saved", "Email sent". |
| `warning` | `AlertTriangle` (lucide) | Attention-needed but non-critical. "Sync slow — your changes will save shortly". |
| `error` | `AlertCircle` (lucide) | Critical error or destructive action notification. "Failed to save — try again". |

The icon mapping mirrors Badge's semantic variant icons where they overlap (`info / success / warning`). PAO
Styling owns the exact colour tokens.

#### 3.3.1 Title vs description-only layout (closes G-NF5)

The toast renders `title` and `description` in a two-line stack when both
are present. When only one is provided:

- **`title` only** — rendered as a single bold line; the description area is absent.
- **`description` only** — rendered as a single non-bold line (same visual weight
  as a description line even though it's the only content). Hosts who want bold
  single-line text should pass it as `title`.

**Recommendation:** always pass `title` for the primary message; use
`description` only for supplementary context. A description-only toast
looks the same visually as a title-only toast but renders with less
visual emphasis — this surprises hosts who put their main message in
`description`. PAO Styling owns the final typography tokens.

### 3.4 Position semantics

The provider's `position` prop controls where all toasts appear. Per-toast
position override is **deferred** — the provider's setting applies to every
toast emitted while it's mounted.

| Position | Common use |
| --- | --- |
| `'bottom-right'` (default) | Harborline standard — non-intrusive corner. |
| `'top-right'` | Common in dashboards where bottom is reserved for status bars. |
| `'bottom-center'` | Mobile-friendly — closer to the user's natural focus on touch. |
| Others | Available but less common. |

### 3.5 Auto-dismiss + max-visible

- Toasts auto-dismiss after `duration` ms (or provider's `defaultDuration`).
- `duration: 0` keeps the toast persistent until manually dismissed.
- When more than `max` toasts are active, the **oldest** is dismissed
  early to make room (FIFO eviction).
- Toasts in different lifecycle phases (entering, visible, exiting) all
  count toward `max`.

#### 3.5.2 WCAG 2.2.1 — error variant MUST NOT auto-dismiss

**This is a Level A WCAG requirement, not a preference.**

Error notifications carry actionable information that users MUST have time
to read and act on. Auto-dismissing an error notification after 4 seconds
violates WCAG 2.2.1 Timing Adjustable — error messages are not "real-time
events" and do not meet any WCAG 2.2.1 exception.

**Implementation rule:**

When `forceErrorPersistent: true` (the default), the implementation MUST
intercept any call to `notify.error()` (or `notify({ variant: 'error' })`)
and **override** the effective `duration` to `0` regardless of any per-toast
`duration` option or the provider's `defaultDuration`. The `duration` prop
is still accepted in the API for documentation purposes but silently clamped
to `0` when `forceErrorPersistent: true` applies.

```tsx
// Both of these MUST result in a persistent error toast:
notify.error('Failed to save — try again')                   // no duration → 0
notify.error({ title: 'Failed', duration: 4000 })           // duration ignored, clamped to 0

// This is the ONLY exception (explicit opt-out with full host responsibility):
<NotificationProvider forceErrorPersistent={false}>
  {/* host must provide its own WCAG-compliant timing mechanism */}
</NotificationProvider>
```

**Consequence for hosts.** Callers MUST manually dismiss error toasts by
calling `notify.dismiss(id)` or the user must press the dismiss button.
The Accessibility contract (§5) requires a dismiss button on all persistent
notifications.

#### 3.5.1 Hover-pause (closes G-NF1)

Sonner (the M2 foundation library) **pauses the auto-dismiss timer when
the user hovers over a toast** and resumes it when the pointer leaves.
This is inherited behaviour — Notification does not need an explicit prop
to enable it; it is on by default.

**Consequence:** a toast with `duration: 4000` that receives a 3-second
hover will remain visible for approximately 4 additional seconds after the
pointer leaves (the timer resumes from where it was paused, not from zero).
Hosts who want hover-pause disabled must override Sonner's internal
`pauseOnHover` option — this is not exposed as a Notification prop in M2
and is deferred (§7).

### 3.6 Imperative API surface

The provider exposes the API via React context, accessed via the
`useNotification()` hook:

```tsx
const notify = useNotification()
notify.success('Invoice saved')
notify.error({
  title: 'Failed to save',
  description: 'Network error. Try again.',
  action: { label: 'Retry', onClick: () => save() },
})
const id = notify.info({ title: 'Syncing…', duration: 0 })
// ...later
notify.dismiss(id)
```

Convenience methods (`success` / `info` / `warning` / `error`) accept
either a string (becomes `title`) or a full `NotificationOptions`.

### 3.7 Dedupe semantics

Calling `notify({ id: 'save-status', variant: 'info', title: 'Saving…' })`
followed by `notify({ id: 'save-status', variant: 'success', title:
'Saved' })` **updates the existing toast** rather than stacking a new one.
This is the canonical pattern for progress-style toasts.

Without an explicit `id`, each call creates a new toast (auto-id).

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `action.onClick` | `()` | The user clicks the inline action button. |
| `onDismiss` | `()` | The toast dismisses — auto-timeout, manual close (X click or swipe), action-click-then-auto-dismiss, dedupe replacement (the toast being replaced fires `onDismiss`), or `dismiss(id)` programmatic call. |

The provider does not expose `onShow` or `onMount` callbacks per-toast in M2.

---

## 5. Slots

Notification has no slot-style extensibility in M2. The content surface is
the prop set (`title`, `description`, `action`). The icon is driven by
`variant`.

Custom icon / arbitrary content slots are deferred (§7).

---

## 6. Component composition

- **Provider placement.** `<NotificationProvider>` wraps the
  application's authenticated routes (or the entire app). It must be
  above every consumer of `useNotification()`.
- **Confirmation patterns.** After saving an entity, the host's mutation
  callback fires `notify.success("Saved")`. The save action stays
  inline; the notification gives passive confirmation.
- **Error-with-retry.** Failed saves emit an `error` toast with an
  `action: { label: 'Retry', onClick: retry }`. The user has time to
  retry before the toast dismisses.
- **Long-running operations.** Hosts emit a persistent (`duration: 0`)
  toast with `variant: 'info'`, capture the returned `id`, then call
  `notify({ id, variant: 'success', title: 'Done' })` to dedupe-update,
  then optionally `notify.dismiss(id)` after a delay.
- **Form validation errors.** Use StatusBanner (in-flow, persistent),
  not Notification — validation errors are not transient.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Per-toast position override** — the provider's `position` applies to
  all toasts in that provider's scope; there is no per-toast position prop
  in M2. **(Closes G-NF3.) Workaround for mixed-position apps:** mount a
  second `<NotificationProvider>` at a different `position` (e.g.
  `position="top-right"`) and route specific notifications through its
  `useNotification()` hook. Both providers render independently into the
  document. A `position` prop on individual `notify()` calls is planned
  for a future wave.
- **Custom icon slot** — toasts use the canonical variant icon only.
- **Custom content slot** — full ReactNode override for the toast body.
  Hosts requiring fully custom toasts can use a separate provider
  layer.
- **Promise-based API** (`notify.promise(p, { loading, success, danger })`)
  — Sonner's signature pattern. Useful but adds surface; deferred.
- **Multiple actions** — current model is exactly 0 or 1 action.
- **Stacking variants** — vertical-only stack in M2; some libraries also
  offer "stacked" (multiple toasts visually stacked into a single
  collapsed surface). Deferred.
- **Swipe-to-dismiss** — Sonner default behaviour. Owned by
  Notification.Interaction.md as an open question.
- **Sound / haptic feedback** — out of scope.
- **Animation timing** — Sonner's default enter animation is a ~150ms
  slide-in from the nearest viewport edge (`bottom-*` positions slide up;
  `top-*` positions slide down). The exit animation is a ~150ms fade +
  slide-out. These timings are not customizable via Notification props in
  M2. PAO Styling owns CSS-override tokens for the animation duration (via
  Sonner's CSS custom properties). **(Closes G-NF4.)**
- **Persistent across page navigation** — provider is React-scoped; SPA
  navigations preserve toasts naturally, full reloads do not. Hosts
  needing cross-reload persistence handle externally.

---

## 8. Open questions (for council)

1. **Imperative vs declarative API.** This spec follows shadcn/Sonner's
   imperative `notify(...)` pattern. An alternative is declarative
   `<Notification open={…} variant={…} />` instances. Leaning
   imperative — notifications are inherently transient + host-event-
   driven; rendering them as JSX is awkward for the typical "fire on
   callback" use case.
2. ~~**Action click → auto-dismiss?**~~ **RESOLVED — auto-dismiss after
   action click** (see §3.2 `action` row). Hosts whose action may fail must
   re-emit. See G-NF2 resolution.
3. **Provider `max` default.** `5` (this spec) or `3`? Many toast
   libraries default to 3. Leaning 5 — Harborline workflows are
   chatty (sync events, save confirmations, etc.); 3 fills too quickly.
4. **Variant naming consistency with Badge.** Badge uses `default / secondary /
   info / success / warning / danger`; Notification uses `info / success /
   warning / error`. Should Notification add a `default` variant for parity?
   Leaning no — every toast is news; a "neutral" toast is just an `info` toast.
   The `error` naming is intentional: toasts announce system errors, not
   destructive-status labels (which stay as `danger` in Badge).
5. **`<NotificationProvider>` singleton enforcement.** Should the
   library warn if multiple providers are mounted? Leaning no — some
   apps may legitimately want nested providers (e.g. embedded
   sub-apps). Hosts deal.
6. **`useNotification()` return.** This spec returns an object with
   convenience methods. An alternative is a single `notify` function
   with `notify(variant, options)`. Leaning object form — more
   discoverable for autocomplete, matches Sonner shape.
7. **Action button styling.** The action label inside a toast — should
   it be a full Button instance, or a styled link? Leaning styled link
   inside the toast — full Button is visually heavy in a small toast.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/notifications/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-NF1 | Critical | Hover-to-pause undocumented | [RESOLVED 2026-06-05] §3.5.1: hover pauses auto-dismiss timer (inherited from Sonner default) |
| G-NF2 | Critical | Action click + auto-dismiss timing unresolved | [RESOLVED 2026-06-05] §8.2 + §3.5: auto-dismiss after action click; host must re-emit toast if async retry fails |
| G-NF3 | High | Per-toast position override workaround undocumented | [RESOLVED 2026-06-05] §7: multiple NotificationProvider instances bind to nearest context; not recommended |
| G-NF4 | High | Animation timing parity | [RESOLVED 2026-06-05] §7 note: Sonner defaults (~150ms slide-in); customization deferred |
| G-NF5 | High | description vs title-only layout impact | [RESOLVED 2026-06-05] §3.2: title-only renders title larger/centered; title+description renders title bold + description small |
| G-NF6 | Medium | Max-stack FIFO eviction silent to AT | [ACCEPTED-RISK 2026-06-05] PAO Accessibility owns; eviction is silent in M1 |
| G-NF7 | Medium | Promise-based API (`notify.promise`) deferred | [ACCEPTED-RISK 2026-06-05] §7: fast-follow after first real save-then-confirm flow |
| G-NF8 | Critical | Error variant auto-dismiss = WCAG 2.2.1 Level A failure. Error messages are "time-limited exceptions" (WCAG 2.2.1) — they MUST NOT auto-dismiss. `notify.error()` inherited `defaultDuration: 4000` from the provider, meaning every error toast auto-dismissed in 4s. | [RESOLVED 2026-06-06] §3.5.2 + `NotificationProviderProps.forceErrorPersistent` (default `true`): error-variant duration is clamped to 0 unless host explicitly opts out with `forceErrorPersistent={false}`. |
