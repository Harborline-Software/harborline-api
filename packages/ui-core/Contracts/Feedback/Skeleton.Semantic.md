# Skeleton — Semantic Contract

- **Component:** Skeleton
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Skeleton.Interaction.md) · [Accessibility](./Skeleton.Accessibility.md) · [Styling](./Skeleton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Skeleton.tsx`
- **Catalog row:** #118 Skeleton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` with pulse animation
- **Interaction-class:** display-only (no user interaction; render-only component)

---

## 1. Component purpose

**Skeleton** — an animated placeholder element used as a loading state stand-in. Renders a single `<div>` with a pulsing gray background.

---

## 2. Props

```typescript
type SkeletonProps = React.HTMLAttributes<HTMLDivElement>
```

All HTML `<div>` attributes are accepted — callers control size, shape, and layout via `className`.

---

## 3. Usage pattern

Callers compose multiple `<Skeleton>` elements to match the shape of the content being loaded:

```tsx
<div className="flex flex-col gap-2">
  <Skeleton className="h-4 w-3/4" />
  <Skeleton className="h-4 w-1/2" />
</div>
```

---

## 4. Wave-N expansion — shape enum + animation type (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (Skeleton 25% coverage)
**Rulings applied:** FR-3 (size axis applied to shape tokens)

### 4.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| No `shape` prop (`circle`/`text`/`rectangle`) | P1 | `shape` prop added — §4.2 |
| No `animation` type enum (`wave`/`pulse`) | P1 | `animation` prop extended — §4.3 |
| No `style` prop spec (sizing is implicit via className) | P2 | Note — §4.4 |

### 4.2 Updated data model

```typescript
// Shape vocabulary
// text      — short-height line (typical for body text rows); renders as a
//             rounded rectangle at reduced height (default h: ~1rem)
// rectangle — arbitrary rectangle; caller controls dimensions via className or style
// circle    — perfect circle; width === height; useful for avatars, icons
type SkeletonShape = 'text' | 'rectangle' | 'circle'

// Animation vocabulary
// pulse — opacity pulse (current implementation: `animate-pulse`)
// wave  — shimmer sweep left-to-right (gradient wash; Kendo default animation)
// none  — no animation (for reduced-motion; also useful for controlled contexts)
type SkeletonAnimation = 'pulse' | 'wave' | 'none'

interface SkeletonProps extends React.HTMLAttributes<HTMLDivElement> {
  // --- existing (kept) ---
  // All HTMLAttributes spread to root <div>; className for sizing (unchanged)

  // --- wave-N additions ---
  /** Named shape. Drives default dimensions and border-radius.
   *  Kendo: `shape` ('circle' | 'text' | 'rectangle').
   *  Default: 'rectangle' — preserves current behaviour (arbitrary div). */
  shape?: SkeletonShape

  /** Animation style. Default: 'pulse' — preserves current `animate-pulse` behaviour.
   *  'wave' adds a shimmer sweep keyframe (PAO Styling authors the gradient animation).
   *  'none' disables animation — always set when `prefers-reduced-motion: reduce`;
   *  the implementation MUST auto-downgrade to 'none' on reduced-motion media query
   *  regardless of the prop value (honoring WCAG 2.2 SC 2.3.3). */
  animation?: SkeletonAnimation

  /** Explicit width override. Kendo Skeleton uses `style` for width/height
   *  on named shapes. Harborline exposes `width` + `height` as explicit props
   *  (converted to inline style) as an alternative to className-based sizing.
   *  Optional — className remains the primary sizing mechanism.
   *  Accepts any CSS length value: '100%', '12rem', '48px'. */
  width?: string | number

  /** Explicit height override. See `width` note. */
  height?: string | number
}
```

### 4.3 Shape semantics and default dimensions

| `shape` | Default `border-radius` | Default dimensions | Token |
|---|---|---|---|
| `rectangle` (default) | `--sf-skeleton-radius-rectangle` (≈ `0.375rem`) | Caller-controlled via className/style | — |
| `text` | `--sf-skeleton-radius-text` (≈ `0.25rem`) | h: `--sf-skeleton-text-height` (≈ `1rem`); w: caller-controlled | `--sf-skeleton-text-height` |
| `circle` | `--sf-skeleton-radius-circle` (`9999px` / `50%`) | If `width` and `height` both absent, defaults to `--sf-skeleton-circle-size` (≈ `2rem`). When only one is set, both axes use the same value. | `--sf-skeleton-circle-size` |

All defaults are PAO Styling tokens; implementors MUST NOT hard-code values.

### 4.4 Animation semantics

| `animation` | Visual effect | CSS mechanism |
|---|---|---|
| `pulse` (default) | Opacity fade in/out | `animate-pulse` (Tailwind built-in) |
| `wave` | Shimmer sweep left → right | Custom keyframe `@keyframes sf-skeleton-wave` with gradient background-position animation (PAO Styling tokens: `--sf-skeleton-wave-duration`, `--sf-skeleton-wave-color-from`, `--sf-skeleton-wave-color-to`) |
| `none` | No animation | No CSS animation applied |

**Reduced-motion rule (mandatory):** regardless of the `animation` prop value, the implementation MUST apply `@media (prefers-reduced-motion: reduce)` to skip all animation (WCAG 2.2 SC 2.3.3). The effective animation when reduced-motion is active is always `none`.

### 4.5 style / width / height sizing notes

Current contract: callers use `className="h-4 w-3/4"` for sizing. This works for Tailwind-class cases but breaks for dynamic/computed sizes (e.g., matching a loaded image's actual pixel dimensions).

Wave-N adds `width` and `height` props as explicit alternatives. When both `className` and `width`/`height` conflict, `width`/`height` wins for the dimension they specify (inline style has higher specificity than class).

Kendo Skeleton passes arbitrary `style` for this use case. Harborline exposes named props rather than a raw `style` override to maintain a documented API surface; the implementation converts them to inline styles.

### 4.6 Composition patterns

```tsx
// Text row group (most common; wave-N version)
<div className="flex flex-col gap-2">
  <Skeleton shape="text" width="75%" animation="wave" />
  <Skeleton shape="text" width="50%" animation="wave" />
</div>

// Avatar + name row
<div className="flex items-center gap-3">
  <Skeleton shape="circle" width="2.5rem" height="2.5rem" animation="wave" />
  <Skeleton shape="text" width="8rem" animation="wave" />
</div>

// Card image placeholder
<Skeleton shape="rectangle" className="aspect-video w-full" animation="wave" />
```

### 4.7 Accessibility note

Skeleton is a decorative loading placeholder. The host SHOULD wrap the skeleton group in a region with `aria-busy="true"` and announce completion via `aria-live="polite"` when content loads. Skeleton itself carries no ARIA role.

### 4.8 Deferred from this wave

- `text` shape multi-line variant (rows count prop) — callers compose multiple `<Skeleton shape="text">` elements.
- Custom wave gradient colours per instance — PAO Styling tokens only.
- Skeleton-as-slot inside DataGrid rows — DataGrid has its own skeleton-row treatment (DataGrid.Semantic.md §6).
