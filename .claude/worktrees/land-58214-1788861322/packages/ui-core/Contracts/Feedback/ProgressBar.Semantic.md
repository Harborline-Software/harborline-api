# ProgressBar — Semantic Contract

- **Component:** ProgressBar
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ProgressBar.Interaction.md) · [Accessibility](./ProgressBar.Accessibility.md) · [Styling](./ProgressBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ProgressBar.tsx`
- **Catalog row:** #100 ProgressBar (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` with width-animation fill

---

## 1. Component purpose

**ProgressBar** — a progress indicator showing a numeric percentage or indeterminate loading state. Supports horizontal and vertical orientation, eight theme colors, and optional label display.

Also exports `ChunkProgressBar` — a segmented variant that divides the bar into discrete `chunks` filled proportionally.

---

## 2. Props

```typescript
type ProgressBarThemeColor =
  'base' | 'primary' | 'secondary' | 'tertiary' |
  'info' | 'success' | 'warning' | 'error'

interface ProgressBarProps {
  value?: number           // 0–100; undefined = indeterminate
  label?: boolean | string // true = show % string; string = custom label
  labelPlacement?: 'start' | 'center' | 'end'  // default: 'end'
  orientation?: 'horizontal' | 'vertical'       // default: 'horizontal'
  themeColor?: ProgressBarThemeColor            // default: 'primary'
  animation?: boolean                           // default: true
  className?: string
}

interface ChunkProgressBarProps extends ProgressBarProps {
  chunks?: number   // default: 5
}
```

---

## 3. Indeterminate mode

`value === undefined` → indeterminate. Fill rendered at 40% width/height with `animate-pulse` instead of `animate-none`.

---

## 4. Value clamping

`pct = Math.max(0, Math.min(100, value))` — clamped to [0, 100].

---

## 5. Wave-N expansion — reverse / min / labelVisible / label component / disabled / style hooks + animation duration (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (ProgressBar 60% coverage)
**Rulings applied:** FR-3 (themeColor axis confirmed; existing `ProgressBarThemeColor` already aligned)

### 5.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| No `reverse` prop | P1 | `reverse` added — §5.2 |
| No `min` prop (custom range floor) | P1 | `min` added — §5.2 |
| `labelVisible` toggle absent | P2 | `labelVisible` added; `label` refactored — §5.3 |
| `label` as custom component | P2 | `label` extended to accept `React.ComponentType` — §5.3 |
| `disabled` prop absent | P2 | `disabled` added — §5.4 |
| `emptyClassName` / `progressClassName` absent | P2 | Both added — §5.5 |
| `animation` boolean only vs `{ duration }` object | P2 | `animation` extended — §5.6 |

### 5.2 Range control: min + reverse

```typescript
interface ProgressBarProps {
  // --- existing (unchanged) ---
  value?:          number                    // undefined = indeterminate
  label?:          boolean | string | React.ComponentType<ProgressBarLabelProps>  // see §5.3
  labelPlacement?: 'start' | 'center' | 'end'  // default: 'end'
  orientation?:    'horizontal' | 'vertical'   // default: 'horizontal'
  themeColor?:     ProgressBarThemeColor        // default: 'primary'
  animation?:      boolean | { duration: number }  // default: true (see §5.6)
  className?:      string

  // --- wave-N additions ---
  /** Minimum value of the range. Default: 0. Combined with `max`, defines
   *  the value range. Fill percentage = (value - min) / (max - min).
   *  Kendo: `min` (default: 0). */
  min?: number

  /** Maximum value of the range. Default: 100.
   *  Kendo: `max` (default: 100). Current clamping is [0, 100]; wave-N
   *  lifts the hardcoded ceiling to max. */
  max?: number

  /** Reverse fill direction. Default: false.
   *  When `true` and `orientation="horizontal"`: fill grows right → left.
   *  When `true` and `orientation="vertical"`: fill grows top → bottom.
   *  Kendo: `reverse`. */
  reverse?: boolean

  /** Disabled state. Default: false. When `true`, the bar renders at
   *  reduced opacity and all interactive ARIA states are suppressed.
   *  Kendo: `disabled`. */
  disabled?: boolean

  /** Additional className applied to the **empty** (background) track segment.
   *  Kendo: `emptyClassName`. Useful for per-instance track customization
   *  without overriding the global token. */
  emptyClassName?: string

  /** Additional className applied to the **filled** progress segment.
   *  Kendo: `progressClassName`. */
  progressClassName?: string
}
```

**Value formula post wave-N:**

```
pct = value === undefined
  ? undefined  // indeterminate
  : Math.max(0, Math.min(1, (value - (min ?? 0)) / ((max ?? 100) - (min ?? 0)))) * 100
```

Fill width/height = `${pct}%`. `reverse` flips the fill direction via CSS `direction: rtl` or `transform: scaleX(-1)` / `scaleY(-1)` (PAO Styling decides).

### 5.3 label prop refactor

Current contract conflates label visibility and content in a single `boolean | string` prop. Wave-N separates concerns:

```typescript
// Custom label render props
interface ProgressBarLabelProps {
  value: number | undefined   // current value (post-clamping)
  min:   number               // effective min
  max:   number               // effective max
  pct:   number | undefined   // fill percentage (0–100) or undefined for indeterminate
}

// label prop now accepts three forms:
// - boolean: true = show default percentage label; false = hide (labelVisible=false shorthand)
// - string:  static custom label text
// - React.ComponentType<ProgressBarLabelProps>: fully custom label component
type ProgressBarLabelProp =
  | boolean
  | string
  | React.ComponentType<ProgressBarLabelProps>
```

New `labelVisible` prop (Kendo parity):

```typescript
/** Controls whether the label region is rendered at all. Default: true.
 *  Kendo: `labelVisible` (default: true).
 *  When `labelVisible=false`, no label DOM node is emitted — cleaner than
 *  `label={false}` which the existing contract also supports. Both forms
 *  produce the same output; `labelVisible` is the preferred vocabulary.
 *  `label={false}` is kept as a deprecated alias (console warning in dev). */
labelVisible?: boolean
```

Label rendering precedence:

1. `labelVisible === false` → no label rendered (takes priority over `label` prop).
2. `label` is a `React.ComponentType` → render the component receiving `ProgressBarLabelProps`.
3. `label` is a `string` → render the string.
4. `label === true` (or `label` omitted, `labelVisible === true`) → render default `${Math.round(pct)}%` string.

### 5.4 Disabled state

When `disabled=true`:

- Root element receives `aria-disabled="true"`.
- Role `progressbar` remains; `aria-valuenow` / `aria-valuemin` / `aria-valuemax` are still emitted (the value is still meaningful when disabled).
- PAO Styling applies `--sf-progressbar-disabled-opacity` (≈ 0.5) to the root.
- `emptyClassName` and `progressClassName` still apply; PAO Styling should account for them in the disabled visual state.

### 5.5 Style hooks (emptyClassName / progressClassName)

The ProgressBar DOM structure:

```html
<div role="progressbar" class="[root] {className}" ...>
  <div class="[empty-track] {emptyClassName}">
    <div class="[progress-fill] {progressClassName}" style="width: {pct}%"></div>
  </div>
  {labelVisible && <span class="[label]">{label}</span>}
</div>
```

`emptyClassName` adds to the track segment; `progressClassName` adds to the fill segment. These props are additive (merged with the base class, not replacing it). Consumers MUST NOT use these props to override token-driven colours — they exist for layout/positioning adjustments that tokens don't cover. Colour overrides go through the `--sf-progressbar-*` token surface.

### 5.6 animation extension

Extended to accept a duration configuration object (Kendo parity):

```typescript
// Wave-N: animation is boolean | { duration: number }
// boolean true/false preserves backward compat (existing usage)
// { duration: number } — duration in milliseconds; Kendo default: 400ms
animation?: boolean | { duration: number }
```

When `animation` is `true` (default), duration uses `--sf-progressbar-animation-duration` token (PAO Styling default: 400ms). When `animation` is `{ duration: 300 }`, that value overrides the token for this instance only.

`prefers-reduced-motion: reduce` always disables animation regardless of `animation` prop value (WCAG 2.2 SC 2.3.3).

### 5.7 ARIA contract post wave-N

```html
<div
  role="progressbar"
  aria-valuemin="{min ?? 0}"
  aria-valuemax="{max ?? 100}"
  aria-valuenow="{value !== undefined ? value : undefined}"
  aria-valuetext="{label as string, if string-typed}"
  aria-disabled="{disabled ? 'true' : undefined}"
  aria-label="{accessible name from host or default 'Loading'}"
>
```

When `value` is `undefined` (indeterminate), `aria-valuenow` is **omitted** (not set to `undefined` string — the attribute must be absent). WCAG 2.2 SC 4.1.2.

### 5.8 Deferred from this wave

- **`emptyStyle` / `progressStyle`** inline style hooks — deferred; className is sufficient for layout adjustments.
- **Stacked progress bars** — multiple fills in one track; deferred to a future DataViz component.
- **Circular progress** — a separate Gauge/RadialProgress component (catalog entry).
- **Value format function** — custom string format for the default label (e.g., `${value} of ${max} items`). Deferred; hosts pass a string or component instead.

---

## 6. Wave-N expansion — ChunkProgressBar separation + orientation + naming (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (ChunkProgressBar 45% coverage)
**Critical finding:** The alias pattern via `type="chunk"` eroneously inherits ProgressBar-specific props that Kendo's separate `ChunkProgressBar` does NOT support.

### 6.1 ChunkProgressBar is a SEPARATE component, not an alias

Current state: `ChunkProgressBar` aliases to `<ProgressBar type="chunk">`. Kendo has ChunkProgressBar as a distinct component. The alias pattern inherited `label`, `labelPlacement`, `labelVisible` — which Kendo's ChunkProgressBar does NOT support.

Wave-N rectification: **ChunkProgressBar becomes a first-class component** with its own prop surface. The `type="chunk"` alias on ProgressBar is DEPRECATED (dev-mode console warning; removed at wave-N+1).

### 6.2 ChunkProgressBar prop surface

```typescript
interface ChunkProgressBarProps {
  // --- value range (same as ProgressBar §5.2) ---
  value?:      number    // 0..max; undefined = indeterminate
  min?:        number    // default: 0
  max?:        number    // default: 100

  // --- chunk-specific ---
  /** Number of equal-width segments to divide the bar into.
   *  Kendo: `chunkCount` (default: 5). Current alias uses `chunks` — wave-N
   *  adds `chunkCount` as canonical; `chunks` deprecated as alias. */
  chunkCount?: number    // default: 5
  /** @deprecated Use `chunkCount`. */
  chunks?:     number

  // --- orientation ---
  /** Kendo: `orientation`. Default: 'horizontal'. */
  orientation?: 'horizontal' | 'vertical'

  // --- reverse ---
  reverse?: boolean      // default: false (see ProgressBar §5.2)

  // --- appearance (FR-3 themeColor; Kendo ChunkProgressBar has themeColor) ---
  themeColor?: ProgressBarThemeColor   // default: 'primary'

  // --- disabled ---
  disabled?: boolean     // default: false

  // --- style hooks ---
  emptyClassName?:    string
  progressClassName?: string

  // --- animation ---
  animation?: boolean | { duration: number }  // default: true

  // --- standard ---
  className?: string
}
```

**ChunkProgressBar intentionally OMITS:** `label`, `labelPlacement`, `labelVisible` — these are ProgressBar-only. Kendo's ChunkProgressBar has no label surface. Hosts who need a label alongside a ChunkProgressBar compose it externally.

### 6.3 chunkCount vs chunks naming reconciliation

| Our old prop | Kendo prop | Wave-N canonical | Migration |
|---|---|---|---|
| `chunks` | `chunkCount` | `chunkCount` | `chunks` deprecated, console warning, removed wave-N+1 |

The old alias file also used `value={3} max={5}` — meaning `max` as chunk count ceiling. This was a semantic error (max is the value ceiling, not the chunk count). Wave-N clarifies:

- `max={100}` is the value range ceiling.
- `chunkCount={5}` is the number of visual segments.
- `value={3}` means 3% of the 0–100 range (3 of 100), NOT "3 of 5 chunks".

For "3 of 5 chunks filled" the correct usage is:
```tsx
<ChunkProgressBar value={3} max={5} chunkCount={5} />
```
Or with explicit range:
```tsx
<ChunkProgressBar value={60} max={100} chunkCount={5} />  // 3 of 5 filled = 60%
```

### 6.4 Fill percentage for ChunkProgressBar

Same formula as ProgressBar §5.2:
```
pct = (value - min) / (max - min) * 100
chunksFilledFloat = pct / 100 * chunkCount
```
Full chunks filled: `Math.floor(chunksFilledFloat)`. Partial fill of the next chunk: PAO Styling decision (Kendo fills the next chunk fully at 50% threshold; exact behaviour token-driven).

### 6.5 ARIA contract

```html
<div
  role="progressbar"
  aria-valuemin="{min ?? 0}"
  aria-valuemax="{max ?? 100}"
  aria-valuenow="{value}"
  aria-disabled="{disabled ? 'true' : undefined}"
  aria-label="{accessible name from host}"
  aria-orientation="{orientation === 'vertical' ? 'vertical' : undefined}"
>
```

`aria-orientation="vertical"` is set when `orientation="vertical"` (WAI-ARIA progressbar role allows this attribute).

### 6.6 ChunkProgressBar.Semantic.md alias redirect update

The existing `ChunkProgressBar.Semantic.md` file (currently a pure alias redirect) SHOULD be updated to reference this section as the canonical spec at wave-N. The redirect remains until implementation removes the `type="chunk"` alias.

### 6.7 Deferred from this wave

- **Partial fill styling of the current chunk** — the chunk fills fully at threshold; gradient fill within a chunk is deferred.
- **Chunk gap customization** — gap size between segments is a token (`--sf-chunk-bar-gap`); per-instance override deferred.
- **Vertical ChunkProgressBar with label** — not supported; label is a ProgressBar-only feature.
