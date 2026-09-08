# Badge — Semantic Contract

- **Component:** Badge
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Badge.Interaction.md) · [Styling](./Badge.Styling.md) · [Accessibility](./Badge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Badge.tsx`
- **Catalog row:** #9 Badge (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** shadcn/ui Badge → native `<span>` with variant token surface

---

## 1. Purpose

Badge is the canonical compact-status-marker primitive of `@harborline-software/ui-react`.
It renders a small pill of text (or text + icon) that conveys an attribute,
status, or count next to a parent entity — e.g. an invoice's status next to
its number, a vendor's verification state, an unread-count next to a tab.

This is a **forward-spec**: Badge has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the shadcn/ui Badge pattern (a styled `<span>` with variant-driven
token surface) adapted to Harborline's status vocabulary.

Badge is **presentational and stateless** — no internal state, no events. It
is consumed in three primary positions:

- **Inline next to other text** (e.g. "Invoice INV-2026-… [Paid]").
- **Standalone in a status column** (e.g. DataGrid cell content).
- **Adjacent to an icon or counter** (e.g. a tab with a "23" count).

A related but distinct component, **StatusBanner** (catalog M1), is the
larger full-width banner equivalent; Badge is the inline, compact form.

---

## 2. Data model

Badge has no internal data model.

```typescript
type BadgeVariant =
  | 'default'
  | 'secondary'
  | 'info'
  | 'success'
  | 'warning'
  | 'danger'

type BadgeSize = 'sm' | 'md'

type BadgeAppearance = 'solid' | 'subtle' | 'outline'

type BadgeShape = 'rounded' | 'pill' | 'square'

interface BadgeProps {
  variant?: BadgeVariant
  size?: BadgeSize
  appearance?: BadgeAppearance
  shape?: BadgeShape
  leadingIcon?: React.ReactNode
  trailingIcon?: React.ReactNode
  children: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'default' \| 'secondary' \| 'info' \| 'success' \| 'warning' \| 'danger'` | `'default'` | Semantic role of the badge. Drives the colour family (PAO Styling owns hue tokens). |
| `size` | `'sm' \| 'md'` | `'md'` | Two sizes. `'sm'` is the dense-content size (table cells, dense lists); `'md'` is the default. |
| `appearance` | `'solid' \| 'subtle' \| 'outline'` | `'subtle'` | The fill treatment. `'subtle'` is the default — pastel fill + dark text. `'solid'` is full saturation. `'outline'` is bordered + transparent. |
| `shape` | `'rounded' \| 'pill' \| 'square'` | `'rounded'` | The border-radius treatment. `'rounded'` is the default (small radius). `'pill'` is fully rounded (max radius). `'square'` is no radius. PAO Styling owns the exact token values. |
| `leadingIcon` | `ReactNode` | — | Optional icon before the text content. Typical: a status glyph (✓, ⚠, ●). |
| `trailingIcon` | `ReactNode` | — | Optional icon after the text content. Less common; used for "dismissible" Badge or count-with-glyph patterns (the dismiss button itself is **not** built into Badge — see §7). |
| `children` | `ReactNode` | _required_ | Badge content. Typically a single short string (1–3 words or a count). Long strings are not wrapped — overflow is host-owned. |

### 3.1 Variant semantics

| Variant | Intent | Typical use |
| --- | --- | --- |
| `default` | Default; no special status. | Generic category labels, "Draft", informational tags. |
| `secondary` | Highlight unrelated to a status — brand-coloured secondary emphasis. | "New", "Beta", "Featured". |
| `info` | Informational status — neither positive nor warning. | "Scheduled", "In review". |
| `success` | Positive, completed, or healthy status. | "Paid", "Active", "Verified". |
| `warning` | Attention-needed but not critical. | "Pending", "Past due (recent)", "Needs review". |
| `danger` | Critical failure or destructive state. | "Overdue", "Failed", "Suspended". |

**Note on `danger` vs `error` vocabulary (closes G-BD4):** Badge uses
`danger` for the red critical variant; Notification uses `error`. This
is an intentional divergence: `danger` labels a persistent *status* ("this
invoice is overdue"); `error` announces a *transient system event* ("the
save failed"). The distinction is semantic, not visual — both render in
red. When a host needs red in both Badge and Notification, use `danger`
for the Badge and `error` for the Notification. See also
`Notification.Semantic.md §8.4`.

### 3.2 Appearance semantics

| Appearance | Fill | Border | Text |
| --- | --- | --- | --- |
| `solid` | Variant-hue fill (full saturation) | None | Contrasting text (typically white) |
| `subtle` | Variant-hue tint (low saturation) | None | Variant-hue text (dark) |
| `outline` | Transparent | Variant-hue border | Variant-hue text |

PAO Styling owns the exact tokens; this contract names the families.

### 3.3 Shape semantics

| Shape | Border-radius | Typical use |
| --- | --- | --- |
| `rounded` | Small radius (default) | Most badges — slight softening without the pill effect. |
| `pill` | Fully rounded (max radius) | Count badges, "new" labels, filter chips. |
| `square` | No radius | Monospace / numeric badge in dense tables. |

`appearance` and `shape` are orthogonal — any combination is valid.

### 3.4 Sizing

- `sm` — text size token ≈ `text-xs`, padding ≈ `px-2 py-0.5`.
- `md` — text size token ≈ `text-sm`, padding ≈ `px-2.5 py-0.5`.

PAO Styling owns the precise tokens.

### 3.5 HTML attribute passthrough

Badge **spreads** additional valid HTML attributes onto the root `<span>`
element. This includes `id`, `data-*`, `aria-*`, `title`, `role`, etc.

Following the Button precedent (Button.Semantic.md §3.5), passthrough is
established as the M2-onward default.

### 3.6 Element choice

Badge renders a `<span>` by default (inline by nature). It is **not** a
button — Badge is presentational. Hosts that need a clickable badge (e.g.
a filter chip the user can dismiss) should compose Badge inside a Button
or wrap the Badge text in their own interactive element. A dedicated
"Chip" component (catalog #25) is the canonical clickable variant —
deferred to a later wave.

---

## 4. Events — semantics

Badge has **no events**. It is presentational and emits no callbacks.

If host code passes `onClick` via HTML attribute passthrough, the underlying
`<span>` will fire it — but Badge's contract does not define this as part
of the supported surface. Clickable status markers should use Chip (future)
or compose Button + Badge.

---

## 5. Slots

Badge's slot model is small and prop-driven:

| Slot prop | Purpose |
| --- | --- |
| `leadingIcon` | Icon before text content. |
| `trailingIcon` | Icon after text content. |
| `children` | The badge label / content. |

Named slots (header / footer / etc.) do not apply.

---

## 6. Component composition

- **DataGrid cells.** Badge is the canonical content for status / state
  columns. Hosts return `<Badge variant="success">Paid</Badge>` from
  their `ColumnDef.cell` render functions.
- **Inline with text.** Badge composes inline with surrounding `<span>` /
  `<p>` text without layout surprises.
- **Tab counts.** TabStrip (M2 Batch B) consumes Badge inside its tab
  trigger to render an unread / count marker. The convention is
  `<Badge size="sm" appearance="subtle" variant="default">23</Badge>`.
- **AppBar / SideNav.** Both surfaces may render Badges next to user
  identity, navigation items, or notification icons.
- **EmptyState / StatusBanner.** No Badge usage by default; the larger
  surfaces communicate status via their own treatment.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Dismissible Badge** — built-in close button + `onDismiss`. Tracked
  separately as the Chip (catalog #25) component.
- **Clickable Badge** — a Badge that acts as a filter / toggle. Use Chip
  (future) or compose Button + Badge.
- **Numeric overflow handling** — built-in `99+` truncation for counts above a
  threshold is deferred. **(G-BD3) Host recipe for today:** format the count
  before passing it to `children`:
  ```tsx
  <Badge variant="info">{count > 99 ? '99+' : String(count)}</Badge>
  ```
  Badge renders whatever string you supply — truncation is a string
  transformation, not a visual cap.
- **Overlay positioning (corner badge on an icon) (closes G-BD2)** — Badge
  has no built-in anchor or absolute-positioning mechanism. The common
  pattern of a count or status dot in the top-right corner of an icon
  requires the host to supply the layout:
  ```tsx
  <div className="relative inline-flex">
    <BellIcon className="h-5 w-5" />
    <Badge className="absolute -top-1 -right-1 min-w-[1.25rem] p-0 justify-center"
           size="sm" shape="pill">3</Badge>
  </div>
  ```
  A dedicated `<BadgeAnchor>` wrapper component with positioning + overlap
  semantics built in is deferred to a future wave.
- **Pulsing / animated treatment** — for "live" indicators. PAO Styling
  may define a `live` variant later.
- **Tooltip composition** — Badge does not directly compose Tooltip; hosts
  wrap in their Tooltip primitive (catalog #138, future wave).
- **Dot-only Badge** (no text, only a colour dot) — common in some design
  systems. May arrive as a dedicated `<Dot>` primitive or a Badge
  variant.

---

## 8. Open questions (for council)

1. **Variant vocabulary.** Are `neutral / info / success / warning / danger /
   accent` the right six? Some systems collapse `info` into `neutral` or
   add `pending` as a distinct seventh. Leaning: keep six — they map
   cleanly onto invoice/work-order/vendor statuses without ambiguity.
2. **Default appearance.** This spec defaults to `'subtle'` (pastel fill,
   dark text) which is the visually-lightest treatment. shadcn defaults
   to `'solid'`. Leaning `'subtle'`: badges are typically status-markers
   that should not dominate the parent content visually. (Note: prop vocabulary is `'subtle'` per §3, not `'soft'`.)
3. **Default size.** `'md'` or `'sm'`? Most badges in ERP-style apps are
   inline with body text and feel oversized at `md`. Leaning `'md'` for
   the canonical default with `'sm'` as the table-cell common case.
4. **`accent` semantics.** Is `accent` clear, or should we rename to
   `brand` or `featured`? Some teams expect `accent` to mean "primary
   accent color" (a UI concept); others read it as "highlighted /
   featured" (a status concept). Council confirm.
5. **Clickable badges.** Should Badge support `onClick` directly (with
   an `interactive?: boolean` opt-in) or require composing inside Button
   / Chip? Leaning: require composition — Badge stays presentational, a
   future Chip handles the interactive case.
6. **Element choice.** Badge defaults to `<span>` (inline). Should an
   `asChild` / Slot opt-in be provided so hosts can render as `<a>` or
   their own element? Leaning: defer — adding it later is non-breaking,
   and hosts can wrap externally for now.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/badges/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BD1 | Critical | Badge-on-parent overlay positioning missing | [RESOLVED 2026-06-05] §7 + §8: inline-only in M1; overlay pattern requires host absolute-positioning |
| G-BD2 | High | ShowCutoutBorder parity absent | [RESOLVED 2026-06-05] Contingent on G-BD1; not applicable in inline-only mode |
| G-BD3 | High | Numeric overflow ("99+") handling absent | [RESOLVED 2026-06-05] §7: host formats outside Badge; if count may exceed 2 digits, use "{Math.min(count,99)}+" |
| G-BD4 | High | variant taxonomy mismatch (danger vs error) | [RESOLVED 2026-06-05] §3 note: `danger` in Badge maps to `error` in Notification/Alert; intentional divergence |
| G-BD5 | Medium | Default appearance subtle vs solid | [ACCEPTED-RISK 2026-06-05] §8.2: OQ resolved — default is subtle |
| G-BD6 | Medium | accent variant reference in §8.4 spurious | [ACCEPTED-RISK 2026-06-05] §8.4 removed (was copy-paste artifact) |

---

## 16. Wave-N expansion — overlay Badge primitive + BadgeContainer (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (Badge 20% coverage)
**Rulings applied:** FR-3 (appearance axes), FR-2 (focus/event passthrough)
**DataGrid pattern:** catalog #1022 — Badge-in-cell with BadgeContainer anchor

### 16.1 Motivation

The current Badge contract covers the **inline / presentational** use-case (status text next to content). The audit found the entire **overlay badge** surface is unspecced — specifically Kendo's `Badge` + `BadgeContainer` pattern that anchors a badge in one of nine positions around a wrapped element (icon, avatar, tab). NumberBadge and NotificationDot both implement subsets of this pattern with hard-coded positions; this section closes the gap with a generic surface that both components can re-base on.

### 16.2 New type definitions

```typescript
// Overlay position axes — FR-3 vocabulary aligned
type BadgeAlign = {
  horizontal: 'start' | 'end'   // default: 'end'
  vertical:   'top'   | 'bottom' // default: 'top'
}

// FR-3 appearance axes — Badge adopts the full set
type BadgeFillMode  = 'solid' | 'outline' | 'flat'  // FR-3
type BadgeRounded   = 'none'  | 'sm' | 'md' | 'lg' | 'full'  // FR-3

// Position of the badge relative to the container edge
type BadgePosition  = 'edge' | 'outside' | 'inside'
// edge    — badge centre sits on the container corner (default; Kendo default)
// outside — badge fully outside the container boundary
// inside  — badge fully inside the container boundary

// themeColor — FR-3 8-value set; omitting means 'base' (neutral)
type BadgeThemeColor =
  | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error'
  | 'inverse'

interface BadgeContainerProps {
  children: React.ReactNode    // the anchor element (icon, avatar, button-content)
  className?: string
}

// Additions to BadgeProps for overlay mode
interface BadgeOverlayProps {
  /** FR-3 fillMode axis. Replaces current `appearance` prop at wave-N migration.
   *  'solid' = saturated fill (current `appearance="solid"`)
   *  'outline' = transparent + border (current `appearance="outline"`)
   *  'flat' = minimal / no border (new; maps to current `appearance="subtle"`)
   *  Migration: `appearance` deprecated aliases to `fillMode` with a
   *  console warning until wave-N+1 removal. */
  fillMode?: BadgeFillMode

  /** FR-3 rounded axis. Replaces current `shape` prop at wave-N migration.
   *  'none' = no radius (current `shape="square"`)
   *  'sm','md','lg' = progressive radius (current `shape="rounded"` ≈ 'md')
   *  'full' = pill (current `shape="pill"`)
   *  Migration: `shape` deprecated at same time. */
  rounded?: BadgeRounded

  /** FR-3 themeColor axis. Extends current `variant` by adding the
   *  standard 8-value palette. At wave-N the existing `variant` prop
   *  remains on the inline Badge (status-semantics vocabulary); `themeColor`
   *  is the overlay / overlay-anchor vocabulary. Both props MUST NOT be
   *  set simultaneously — `themeColor` wins if both are present. */
  themeColor?: BadgeThemeColor

  /** Overlay positioning — only meaningful when Badge is rendered inside
   *  a BadgeContainer. Ignored in inline mode. */
  align?: BadgeAlign
  position?: BadgePosition

  /** Cutout border — renders a thin ring of the host background colour
   *  between the badge and the container edge, providing visual separation
   *  on coloured or photo backgrounds. Kendo: `cutoutBorder`. */
  cutoutBorder?: boolean
}
```

### 16.3 BadgeContainer wrapper

`BadgeContainer` is a layout wrapper that supplies `position: relative; display: inline-flex` around its `children`, eliminating the host responsibility for the `relative` wrapper documented in §7 (Deferred — overlay pattern). It is the semantic anchor for any overlay Badge.

```tsx
// Canonical usage (DataGrid #1022 pattern — status dot on a row icon)
<BadgeContainer>
  <BellIcon className="h-5 w-5" />
  <Badge
    themeColor="error"
    fillMode="solid"
    rounded="full"
    position="edge"
    align={{ horizontal: 'end', vertical: 'top' }}
    cutoutBorder
  >
    3
  </Badge>
</BadgeContainer>

// NumberBadge rebased on BadgeContainer (wave-N shape)
<BadgeContainer>
  <MessagesIcon />
  <Badge themeColor="primary" fillMode="solid" rounded="full" align={{ horizontal: 'end', vertical: 'top' }} position="edge">
    {count > max ? `${max}+` : count}
  </Badge>
</BadgeContainer>

// NotificationDot rebased on BadgeContainer (wave-N shape)
<BadgeContainer>
  <AvatarIcon />
  <Badge themeColor="success" fillMode="solid" rounded="full"
         align={{ horizontal: 'end', vertical: 'top' }} position="edge"
         cutoutBorder
         aria-label="Online" />
</BadgeContainer>
```

### 16.4 Prop migration table (existing Badge props → FR-3 axes)

| Old prop | New prop | Migration |
|---|---|---|
| `appearance="solid"` | `fillMode="solid"` | Deprecated alias |
| `appearance="outline"` | `fillMode="outline"` | Deprecated alias |
| `appearance="subtle"` | `fillMode="flat"` | Deprecated alias |
| `shape="rounded"` | `rounded="md"` | Deprecated alias |
| `shape="pill"` | `rounded="full"` | Deprecated alias |
| `shape="square"` | `rounded="none"` | Deprecated alias |
| `variant` | `variant` (kept) + `themeColor` (new for overlay) | No change for inline; `themeColor` for overlay contexts |

Deprecated props emit a console warning in dev mode. Both forms accepted until wave-N+1.

### 16.5 NumberBadge + NotificationDot rebasing

At wave-N, NumberBadge and NotificationDot become **thin wrappers over BadgeContainer + Badge** rather than independent implementations:

- **NumberBadge** responsibility: `count` / `max` overflow logic + `aria-label` defaulting. Renders as `<BadgeContainer><Badge fillMode="solid" rounded="full" themeColor={variantToThemeColor(variant)} align={…} position="edge" cutoutBorder>{formatted}</Badge>{children if provided}</BadgeContainer>`.
- **NotificationDot** responsibility: binary visibility (`visible` prop) + `pulse` animation flag. Renders as `<BadgeContainer><Badge fillMode="solid" rounded="full" themeColor={colorToThemeColor(color)} align={{ horizontal: 'end', vertical: 'top' }} position="edge" cutoutBorder aria-label={ariaLabel} /></BadgeContainer>`.

Both components KEEP their current prop surface (backward compatible). Internal implementation changes only.

### 16.6 Accessibility notes for overlay mode

- Badge in overlay mode carries `aria-label` when text content is absent (dot-only). Required.
- Badge in overlay mode that is purely decorative (sighted UX only, context already described by surrounding text) SHOULD receive `aria-hidden="true"`.
- `BadgeContainer` emits no ARIA of its own — it is a layout primitive.
- `cutoutBorder` is purely visual; no ARIA implication.

### 16.7 Deferred from this wave

- **Animated count increment** — NumberBadge count-change animation.
- **Badge inside `<td>` without BadgeContainer** — DataGrid cell authors use BadgeContainer within their cell renderer.
- **RTL `align` mirroring** — `horizontal: 'end'` SHOULD auto-flip on RTL documents. Deferred to i18n wave.
- **Zero-state (count=0) display** — NumberBadge still renders null; no zero pill. Deferred.
