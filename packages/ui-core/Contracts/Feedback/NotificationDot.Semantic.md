# NotificationDot — Semantic Contract

- **Component:** NotificationDot
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NotificationDot.Interaction.md) · [Accessibility](./NotificationDot.Accessibility.md) · [Styling](./NotificationDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NotificationDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<span>` dot indicator

---

## 1. Purpose

NotificationDot is a **wrapper that overlays a small colored dot badge** on
any child element to signal the presence of new activity, unread items, or
an alert state. It is a presence indicator (binary: dot shown or not), not
a count badge — use NumberBadge for numeric counts.

---

## 2. Data model

```typescript
type NotificationDotColor = 'default' | 'primary' | 'danger' | 'warning' | 'success'

interface NotificationDotProps {
  visible?: boolean
  color?: NotificationDotColor
  pulse?: boolean
  'aria-label'?: string
  children: React.ReactNode
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `visible` | `boolean` | `true` | Whether the dot badge is shown. When `false`, only `children` renders. |
| `color` | `NotificationDotColor` | `'danger'` | Semantic color of the dot. |
| `pulse` | `boolean` | `false` | When `true`, adds an animated ping ring around the dot for urgency. |
| `aria-label` | `string` | — | Accessible label for the dot. When provided, the dot span gets `role="status"`. |
| `children` | `ReactNode` | _required_ | The element to overlay the dot on (e.g., an icon button, avatar). |
| `className` | `string` | — | Additional classes on the root wrapper `<div>`. |

### 3.1 Color semantics

| `color` | Semantic |
| --- | --- |
| `default` | Neutral presence indicator |
| `primary` | Primary activity (new messages) |
| `danger` | Error or critical alert |
| `warning` | Warning or attention needed |
| `success` | Positive status |

### 3.2 Pulse animation

When `pulse=true`, a second `<span>` with `animate-ping` is rendered behind
the dot to create a ripple effect, signaling active or urgent notifications.

---

## 4. Events

Display-only — no events.

---

## 5. Composition

### Icon button with unread indicator

```tsx
<NotificationDot visible={hasUnread} color="primary" aria-label="Unread messages">
  <MessagesIcon />
</NotificationDot>
```

### Critical alert with pulse

```tsx
<NotificationDot visible={hasAlerts} color="danger" pulse aria-label="Critical alerts">
  <BellIcon />
</NotificationDot>
```

---

## 6. Deferred features (original)

- **Position variants** — dot is always top-right (-1/-1 offset); no position
  prop.
- **Size variants** — dot size is fixed at `h-2.5 w-2.5`; no `size` prop.

---

## 7. Wave-N expansion — align / position / fillMode / rounded / themeColor + BadgeContainer rebasing (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (NotificationDot 40% coverage)
**Rulings applied:** FR-3 (appearance axes)
**Rebasing target:** Badge.Semantic.md §16 (BadgeContainer + overlay primitive)

### 7.1 Gaps addressed

| Gap (audit) | Priority | Resolution in this section |
|---|---|---|
| `align` object | P1 | `align` prop added (default: `{ horizontal: 'end', vertical: 'top' }`) |
| `cutoutBorder` | P1 | `cutoutBorder` prop added (default `true`) |
| `fillMode` (FR-3) | P2 | `fillMode` added; default `'solid'` |
| `rounded` (FR-3) | P2 | `rounded` added; default `'full'` |
| `position` variants | P1 | `position` prop added; default `'edge'` |
| `themeColor` 8-value set | P2 | `themeColor` maps from `color`; direct override available |

### 7.2 Updated data model

```typescript
type NotificationDotColor = 'default' | 'primary' | 'danger' | 'warning' | 'success' // kept

// FR-3 axes (see Badge.Semantic.md §16.2 for full type definitions)
type BadgeAlign    = { horizontal: 'start' | 'end'; vertical: 'top' | 'bottom' }
type BadgeFillMode = 'solid' | 'outline' | 'flat'
type BadgeRounded  = 'none' | 'sm' | 'md' | 'lg' | 'full'
type BadgePosition = 'edge' | 'outside' | 'inside'
type BadgeThemeColor = 'primary' | 'secondary' | 'tertiary' | 'info' | 'success' | 'warning' | 'error' | 'inverse'

interface NotificationDotProps {
  // --- existing (kept) ---
  visible?:      boolean                  // default: true
  color?:        NotificationDotColor     // default: 'danger'
  pulse?:        boolean                  // default: false
  'aria-label'?: string
  children:      React.ReactNode
  className?:    string

  // --- wave-N additions (FR-3) ---
  align?:        BadgeAlign               // default: { horizontal: 'end', vertical: 'top' }
  position?:     BadgePosition            // default: 'edge'
  cutoutBorder?: boolean                  // default: true
  fillMode?:     BadgeFillMode            // default: 'solid'
  rounded?:      BadgeRounded             // default: 'full'
  themeColor?:   BadgeThemeColor          // direct override; takes precedence over `color`

  // --- wave-N: size control (audit P2 gap) ---
  /** Explicit size override. When omitted, defaults to token `--sf-badge-dot-size` (10px).
   *  FR-3 `size` vocabulary (`'sm'|'md'|'lg'`) applies when set. */
  size?:         'sm' | 'md' | 'lg'      // default: implicit token size (≈md)
}
```

### 7.3 Color → themeColor mapping

| `color` | Derived `themeColor` |
|---|---|
| `default` | (base — neutral) |
| `primary` | `'primary'` |
| `danger` | `'error'` |
| `warning` | `'warning'` |
| `success` | `'success'` |

`themeColor` overrides `color` when both present (dev warning emitted).

### 7.4 BadgeContainer rebasing

NotificationDot internally wraps its `children` in `BadgeContainer` (Badge.Semantic.md §16.3). The outer `<div className="relative inline-flex">` host requirement (current implementation) is removed at wave-N — `BadgeContainer` owns that layout.

Existing hosts that supply their own `relative` wrapper will still work correctly (nested stacking contexts are harmless at this scale).

### 7.5 Dot-only (no text) badge

NotificationDot renders a dot with **no visible text**. The `aria-label` prop is REQUIRED for sighted + AT equivalence (SR announces the label). If `aria-label` is absent, a dev-mode warning fires. This is a stricter requirement than Badge inline-mode.

### 7.6 Deferred from this wave

- RTL `align` mirroring.
- Numeric count on NotificationDot (use NumberBadge instead).
