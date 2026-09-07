# NumberBadge — Semantic Contract

- **Component:** NumberBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumberBadge.Interaction.md) · [Accessibility](./NumberBadge.Accessibility.md) · [Styling](./NumberBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NumberBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled numeric badge `<span>`

---

## 1. Purpose

NumberBadge renders a **numeric count indicator** as a circular pill badge.
It is used wherever a count needs to be surfaced inline — unread counts,
queue depths, item totals, alert counts. Renders `null` when `count <= 0`.

NumberBadge vs NotificationDot:

| Criterion | NumberBadge | NotificationDot |
| --- | --- | --- |
| Content | Numeric count | Binary presence only |
| Overflow cap | Yes (`max` prop) | N/A |
| Wraps children | No (standalone) | Yes (wraps any child) |

---

## 2. Data model

```typescript
type NumberBadgeVariant = 'default' | 'primary' | 'danger' | 'warning'
type NumberBadgeSize = 'sm' | 'md'

interface NumberBadgeProps {
  count: number
  max?: number
  variant?: NumberBadgeVariant
  size?: NumberBadgeSize
  'aria-label'?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `count` | `number` | _required_ | The numeric value to display. Renders `null` when `count <= 0`. |
| `max` | `number` | `99` | Maximum displayed count. When `count > max`, displays `"{max}+"`. |
| `variant` | `NumberBadgeVariant` | `'default'` | Color scheme. |
| `size` | `NumberBadgeSize` | `'md'` | Size variant. |
| `aria-label` | `string` | — | Custom accessible label. Defaults to `"{count} notification(s)"`. |
| `className` | `string` | — | Additional classes. |

### 3.1 Variant semantics

| `variant` | Semantic |
| --- | --- |
| `default` | Neutral count (gray) |
| `primary` | Primary activity count (blue) |
| `danger` | Error or urgent count (red) |
| `warning` | Warning count (amber) |

### 3.2 Overflow display

When `count > max`: displays `"{max}+"` (e.g., "99+").
Accessible label still uses the actual `count` value.

---

## 4. Events

Display-only — no events.

---

## 5. Composition

### Unread count in a tab label

```tsx
<span>Invoices <NumberBadge count={pendingCount} variant="warning" /></span>
```

### Notification count (overflow)

```tsx
<NumberBadge count={unreadCount} max={9} variant="danger" aria-label={`${unreadCount} unread notifications`} />
```

---

## 6. Deferred features

- **Zero-state display** — renders `null`; no "0" state.
- **Animated count change** — no transition on count increment.

---

## 7. Wave-N expansion — align / position / fillMode / rounded / themeColor + BadgeContainer rebasing (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (NumberBadge 35% coverage)
**Rulings applied:** FR-3 (appearance axes)
**Rebasing target:** Badge.Semantic.md §16 (BadgeContainer + overlay primitive)

### 7.1 Gaps addressed

| Gap (audit) | Priority | Resolution in this section |
|---|---|---|
| `align` — position control | P1 | `align` prop added (defaults to `{ horizontal: 'end', vertical: 'top' }`) |
| `cutoutBorder` — ring between badge and anchor | P1 | `cutoutBorder` prop added (default `true` for NumberBadge — numeric badges almost always need visual separation) |
| `fillMode` (FR-3) | P2 | `fillMode` replaces old `appearance`; default `'solid'` (number badges are always solid) |
| `rounded` (FR-3) | P2 | `rounded` replaces implicit `'full'` assumption; default `'full'` preserved |
| `position` — edge/outside/inside | P1 | `position` prop added; default `'edge'` |
| `themeColor` 8-value set | P2 | See §7.3 — `variant` maps onto `themeColor`; `themeColor` direct override added |
| Zero-state display | P1 | See §7.4 |
| Animated count change | P2 | See §7.5 |

### 7.2 Updated data model

```typescript
type NumberBadgeVariant = 'default' | 'primary' | 'danger' | 'warning'  // kept
type NumberBadgeSize    = 'sm' | 'md'                                     // kept

// FR-3 axes (see Badge.Semantic.md §16.2 for full type definitions)
type BadgeAlign    = { horizontal: 'start' | 'end'; vertical: 'top' | 'bottom' }
type BadgeFillMode = 'solid' | 'outline' | 'flat'
type BadgeRounded  = 'none' | 'sm' | 'md' | 'lg' | 'full'
type BadgePosition = 'edge' | 'outside' | 'inside'
type BadgeThemeColor = 'primary' | 'secondary' | 'tertiary' | 'info' | 'success' | 'warning' | 'error' | 'inverse'

interface NumberBadgeProps {
  // --- existing (kept) ---
  count:         number
  max?:          number          // default: 99
  variant?:      NumberBadgeVariant  // default: 'default'
  size?:         NumberBadgeSize     // default: 'md'
  'aria-label'?: string
  className?:    string
  children?:     React.ReactNode    // NEW — anchor element; when provided,
                                    // renders inside BadgeContainer

  // --- wave-N additions (FR-3) ---
  align?:        BadgeAlign         // default: { horizontal: 'end', vertical: 'top' }
  position?:     BadgePosition      // default: 'edge'
  cutoutBorder?: boolean            // default: true
  fillMode?:     BadgeFillMode      // default: 'solid'
  rounded?:      BadgeRounded       // default: 'full'
  themeColor?:   BadgeThemeColor    // override; takes precedence over variant
}
```

### 7.3 Variant → themeColor mapping

When `themeColor` is omitted, the existing `variant` drives colour via this mapping:

| `variant` | Derived `themeColor` |
|---|---|
| `default` | (base — neutral; omit themeColor) |
| `primary` | `'primary'` |
| `danger` | `'error'` |
| `warning` | `'warning'` |

Direct `themeColor` prop overrides `variant`. Both MUST NOT be set simultaneously in host code; `themeColor` wins if both are present (dev warning emitted).

### 7.4 Zero-state display

`count <= 0` still renders `null` by default. New wave-N opt-in:

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `showZero?` | `boolean` | `false` | When `true`, renders the badge with `'0'` content even when `count === 0`. Useful for persistent badge anchors in navigation that should not shift layout when count drops to zero. |

### 7.5 Animated count change

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `animated?` | `boolean` | `false` | When `true`, count transitions animate via a brief scale-pop (CSS transform: `scale(1.3) → scale(1)` over 150ms). Respects `prefers-reduced-motion: reduce` (animation skipped). |

### 7.6 Standalone vs anchor mode

NumberBadge operates in two modes based on whether `children` is provided:

- **Standalone** (`children` absent): renders as a bare badge `<span>` with no container. Hosts own positioning. Backward-compatible with current usage.
- **Anchor** (`children` present): renders `<BadgeContainer><{children} /><badge …></BadgeContainer>`. The badge is absolutely positioned relative to the container. `align`, `position`, and `cutoutBorder` are active.

### 7.7 Deferred from this wave

- RTL `align` mirroring (i18n wave).
- Custom `max` format function (e.g., `1k+` for large counts). Host formats `children` string.
