# Notification — Styling Contract

- **Component:** Notification (Toast)
- **ADR 0017 family:** Feedback
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Notification.Semantic.md) · [Interaction](./Notification.Interaction.md) · [Accessibility](./Notification.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Sonner / Radix Toast_
- **Catalog row:** #89 Notification / Toast (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Toast / shadcn Sonner`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Notification (also known as Toast) is a transient, non-modal message that
appears at the edge of the viewport to communicate a system status update
or feedback for an asynchronous action. It is distinct from StatusBanner
(which is a persistent in-flow message) and from Dialog (which is modal
and demands user response).

This contract names the `--sf-notification-*` token surface (intent, size,
placement, dismiss affordance) and pins the Tailwind class recipes for the
M2 default layer. Notification composes on shadcn Sonner OR Radix Toast
primitives (Engineer choice; the token surface is foundation-agnostic).

---

## 2. Token surface

Notification exposes the following CSS custom properties (the
`--sf-notification-*` family). Adapters MUST consume these tokens;
adapters MUST NOT hard-code values in component code.

### 2.1 Intent axis (variant)

Four intents — same vocabulary as StatusBanner but visually compact.

| Token | Semantic role | Varies by intent | Notes |
|---|---|---|---|
| `--sf-notification-bg-{intent}` | Background fill | yes | Tinted surface (paired with `fg` at ≥ 4.5:1) OR solid intent colour (paired with white `fg`) — variant choice |
| `--sf-notification-fg-{intent}` | Foreground colour for message + icon glyph | yes | Pairs with `--sf-notification-bg-{intent}` at ≥ 4.5:1 |
| `--sf-notification-border-{intent}` | Border colour OR left-accent stripe | yes | Pairs with `--sf-notification-bg-{intent}` at ≥ 3:1 per WCAG 2.2 SC 1.4.11 |
| `--sf-notification-icon-{intent}` | Glyph key for the per-intent icon | yes | Glyph identifier (consumed by IHarborlineIconProvider); rendered colour resolves to `--sf-notification-fg-{intent}` |

The four intents:

| Intent | Use case | Default icon |
|---|---|---|
| `success` | Action completed | check-circle |
| `info` | Informational status | info-circle |
| `warning` | Caution; user attention needed (non-blocking) | exclamation-triangle |
| `error` | Action failed; data may be lost | x-circle |

**Cross-channel signal requirement.** Intent MUST NOT be carried by colour
alone — the notification MUST include either:

a. A semantic title prefix matching the intent ("Success", "Warning",
   "Error"), OR
b. The intent icon (per `--sf-notification-icon-{intent}`).

The contract REQUIRES the icon (a) is non-optional default; the title
prefix is allowed but not required when the icon is present.

### 2.2 Shape tokens

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-notification-radius` | Corner radius | typically `var(--sf-radius-md)` |
| `--sf-notification-padding-x` | Horizontal padding | typically `1rem` |
| `--sf-notification-padding-y` | Vertical padding | typically `0.75rem` |
| `--sf-notification-icon-gap` | Gap between icon and message | typically `0.75rem` |
| `--sf-notification-action-gap` | Gap between message and action button (when present) | typically `0.75rem` |
| `--sf-notification-shadow` | Box shadow | typically `0 10px 15px -3px rgba(17, 24, 39, 0.10), 0 4px 6px -4px rgba(17, 24, 39, 0.10)` — emphasises elevated floating surface |
| `--sf-notification-max-width` | Maximum width | typically `28rem` (448px) |
| `--sf-notification-min-width` | Minimum width | typically `20rem` (320px) |

### 2.3 Dismiss-button tokens

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-notification-dismiss-fg` | Dismiss-button glyph colour | none | Resolves to a muted variant of `--sf-notification-fg-{intent}` |
| `--sf-notification-dismiss-hover-bg` | Dismiss-button background on hover/focus | hover/focus | Low-opacity overlay of `--sf-notification-fg-{intent}` |
| `--sf-notification-dismiss-size` | Dismiss-button tap-target size | none | MUST be ≥ `1.5rem` (24px) per WCAG 2.5.8 |

### 2.4 Stack tokens (toaster region)

The toaster region holds the active notification stack. It sits absolutely
positioned at one of the four corners (or top-/bottom-centre).

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-notification-stack-gap` | Vertical gap between stacked notifications | typically `0.5rem` |
| `--sf-notification-stack-offset-edge` | Distance from viewport edge | typically `1rem` (`bottom-4` / `right-4` etc.) |
| `--sf-notification-stack-max-visible` | Maximum simultaneously visible notifications | typically `5`; older ones collapse beneath or are dismissed |

### 2.5 Animation tokens

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-notification-enter-duration` | Slide-in / fade-in duration | typically `200ms` |
| `--sf-notification-exit-duration` | Slide-out / fade-out duration | typically `150ms` |
| `--sf-notification-auto-dismiss-duration` | Auto-dismiss timeout for non-error intents | typically `5000ms`; ERROR intent should NEVER auto-dismiss |

Animations MUST honor `prefers-reduced-motion: reduce` (see §6).

### 2.6 Token additions to `feedback.tokens.json`

The `--sf-notification-*` family extends the existing `feedback.tokens.json`
file. Recommended namespace addition:

```jsonc
"notification": {
  "_intent": "transient elevated toast; four intents × 24×24 dismiss; auto-dismiss for non-error; reduced-motion compliant",
  "intent": {
    "success": { "bg": "#ECFDF5", "fg": "#065F46", "border": "#10B981", "icon": "check-circle" },
    "info":    { "bg": "#EFF6FF", "fg": "#1E40AF", "border": "#3B82F6", "icon": "info-circle" },
    "warning": { "bg": "#FFFBEB", "fg": "#92400E", "border": "#F59E0B", "icon": "exclamation-triangle" },
    "error":   { "bg": "#FEF2F2", "fg": "#991B1B", "border": "#EF4444", "icon": "x-circle" }
  },
  "radius": "8px",
  "paddingX": "1rem",
  "paddingY": "0.75rem",
  "iconGap": "0.75rem",
  "actionGap": "0.75rem",
  "shadow": "0 10px 15px -3px rgba(17, 24, 39, 0.10), 0 4px 6px -4px rgba(17, 24, 39, 0.10)",
  "maxWidth": "28rem",
  "minWidth": "20rem",
  "dismiss": {
    "fg": "#6B7280",
    "hoverBg": "rgba(107, 114, 128, 0.1)",
    "size": "1.5rem"
  },
  "stack": {
    "gap": "0.5rem",
    "offsetEdge": "1rem",
    "maxVisible": 5
  },
  "animation": {
    "enterDuration": "200ms",
    "exitDuration": "150ms",
    "autoDismissDuration": "5000ms"
  },
  "_notes": {
    "contrast": "Every intent's bg/fg pair verified ≥ 4.5:1 (WCAG 2.2 SC 1.4.3). Every intent's bg/border pair verified ≥ 3:1 (WCAG 2.2 SC 1.4.11). Adapters MUST re-verify if provider override changes defaults.",
    "errorAutoDismiss": "ERROR intent MUST NOT auto-dismiss — error messages need user acknowledgement. Implementations should override autoDismissDuration to null/0/infinity for error intent.",
    "icon": "Glyph identifiers (check-circle, info-circle, exclamation-triangle, x-circle) are consumed by IHarborlineIconProvider. The rendered glyph colour resolves to fg.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Notification body

```jsx
<div role="status" aria-live="polite" class="flex items-start gap-3 rounded-lg border-l-4 bg-green-50 px-4 py-3 shadow-lg min-w-[20rem] max-w-md
  data-[intent=success]:border-green-500 data-[intent=success]:bg-green-50 data-[intent=success]:text-green-900
  data-[intent=info]:border-blue-500    data-[intent=info]:bg-blue-50     data-[intent=info]:text-blue-900
  data-[intent=warning]:border-amber-500 data-[intent=warning]:bg-amber-50 data-[intent=warning]:text-amber-900
  data-[intent=error]:border-red-500    data-[intent=error]:bg-red-50     data-[intent=error]:text-red-900">
  <Icon name={icon} aria-hidden="true" class="h-5 w-5 shrink-0 mt-0.5" />
  <div class="flex-1 min-w-0">
    {title && <p class="font-medium">{title}</p>}
    <p class="text-sm">{message}</p>
    {action && <button class="mt-2 text-sm font-medium underline hover:no-underline">{action.label}</button>}
  </div>
  {dismissable && (
    <button aria-label="Dismiss notification" class="ml-auto h-6 w-6 inline-flex items-center justify-center rounded hover:bg-black/5">
      <XIcon class="h-4 w-4" aria-hidden="true" />
    </button>
  )}
</div>
```

### 3.2 Toaster region (host)

```jsx
<div role="region" aria-label="Notifications" class="fixed bottom-4 right-4 z-[60] flex flex-col-reverse gap-2 pointer-events-none">
  {/* Notifications get pointer-events: auto via their own class */}
</div>
```

The `flex-col-reverse` lets newer notifications appear at the bottom of
the stack (closer to where the user expects them); older notifications
visually rise upward as the stack grows. The host container has
`pointer-events: none` so it doesn't intercept clicks behind it when no
notification is at that pixel; each notification's own root has
`pointer-events: auto`.

### 3.3 Z-index layer

Notifications use `z-60` per AppLayout.Styling §3.3 strata convention
(above modals at `z-50`).

### 3.4 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer.

---

## 4. Visual state inventory

| State | Trigger | Notes |
|---|---|---|
| **entering** | Notification just rendered | Slide-in + fade-in animation per `--sf-notification-enter-duration` |
| **displayed** | Auto-dismiss timer running (non-error) OR persistent (error) | Default visual state |
| **hover** | Pointer over the notification | Pauses the auto-dismiss timer (Interaction §5) |
| **exiting** | Dismiss button activated OR auto-dismiss timer fires | Slide-out + fade-out animation per `--sf-notification-exit-duration` |
| **dismissed** | Animation complete | Removed from DOM |

The Dismiss button has its own state matrix (default / hover / focus-
visible) per §2.3.

---

## 5. Responsive behaviour

| Viewport | Behaviour |
|---|---|
| Mobile (< 640px) | Notifications occupy ~95% of the viewport width (with edge padding); typical placement: bottom-centre |
| Tablet / Desktop (≥ 640px) | Notifications respect `--sf-notification-min-width` / `--sf-notification-max-width`; placement: bottom-right (default) or consumer-pinned corner |

Toaster region position is consumer choice (`position` prop on the host);
recommended default is `bottom-right` on desktop, `bottom-center` on
mobile.

---

## 6. Reduced motion

All entrance + exit animations + the auto-dismiss progress indicator (if
present) MUST honor `prefers-reduced-motion: reduce`:

- Entrance: replace slide+fade with instant appearance.
- Exit: replace slide+fade with instant disappearance.
- Auto-dismiss progress bar (if implemented): MAY stay (it's an
  informational indicator, not animation-for-animation's-sake) but
  SHOULD reduce or eliminate any pulsing/breathing animation.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Position default — bottom-right vs. top-right.** Mac OS conventions
   place toasts top-right; iOS / Android place them top-centre or
   bottom-centre. Web convention varies. Contract default: bottom-right
   on desktop (matches shadcn Sonner default), bottom-centre on mobile.
2. **Auto-dismiss duration per intent.** Default 5000ms across success /
   info / warning; error NEVER auto-dismisses. Some systems use 3s for
   success and 5s for warning. Council may tune; the contract pins one
   default to start.
3. **Action button styling.** Notifications MAY include a single action
   ("Undo", "Retry"). Default styling: underlined text-link inside the
   notification body; NOT a full Button. This keeps the toast compact.
4. **Progress bar for auto-dismiss timer.** Some designs show a thin
   progress bar decrementing as the timer counts down. Adds visual
   complexity; out of M2 contract scope (consumer opt-in if needed).
5. **Solid vs. tinted background for error intent.** The default uses
   tinted (`bg-red-50`); some systems prefer solid (`bg-red-600 text-
   white`) for error emphasis. Contract: tinted by default (consistent
   with the other intents); engineer + council may swap to solid for
   error if usability evidence supports it.

---

## 8. Do / Don't

### Do

- Pair `--sf-notification-bg-{intent}` and `--sf-notification-fg-{intent}`
  at ≥ 4.5:1.
- Pair `--sf-notification-border-{intent}` against the bg at ≥ 3:1.
- Include the intent icon (`--sf-notification-icon-{intent}`); colour is
  NOT the only channel.
- Pin error intent at NO auto-dismiss.
- Use Sonner / Radix Toast primitives — they handle the elevated stacking
  and the focus-management edge cases for you.
- Honor `prefers-reduced-motion` on entrance + exit animations.

### Don't

- Don't put hex values in component code. Component CSS consumes
  `var(--sf-notification-*)` only.
- Don't auto-dismiss error notifications. Users need to acknowledge.
- Don't ship more than 5 simultaneously visible notifications; collapse
  older ones into a "+ N more" footer or dismiss-by-FIFO.
- Don't stack notifications inside Dialogs — Dialogs already own user
  attention; a toast on top creates focus thrash.
- Don't put critical actions (the only path to an action) ONLY inside
  notification. Auto-dismiss + user-missed = action unavailable. Action
  must be reachable elsewhere too.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Sonner OR Radix Toast
  primitives.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-notification>` is
  one candidate; tokens are framework-neutral.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #89 Notification / Toast (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Sonner — primary React foundation candidate
- Radix Toast — alternative React foundation
- [Notification.Semantic.md](./Notification.Semantic.md) — prop contract
- [Notification.Interaction.md](./Notification.Interaction.md) — timing, swipe-to-dismiss
- [Notification.Accessibility.md](./Notification.Accessibility.md) — role=status / role=alert per intent, ARIA live regions
- `_shared/design/tokens/feedback.tokens.json` — default token values
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
