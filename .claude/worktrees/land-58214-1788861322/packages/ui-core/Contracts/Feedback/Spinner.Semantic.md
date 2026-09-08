# Spinner — Semantic Contract

- **Component:** Spinner
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Spinner.Interaction.md) · [Accessibility](./Spinner.Accessibility.md) · [Styling](./Spinner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Spinner.tsx`
- **Catalog row:** #A24 Spinner (`app-priority: high`, `library-scope: v1`) — shadcn/Radix; inline loading spinner
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled SVG spinner
- **Interaction-class:** display-only (no user interaction; render-only component)

---

## 1. Component purpose

**Spinner** — a rotating loading indicator SVG. Used inline alongside content or overlaid to indicate an in-progress operation.

---

## 2. Props

```typescript
type SpinnerSize = 'xs' | 'sm' | 'md' | 'lg'

interface SpinnerProps extends React.SVGAttributes<SVGElement> {
  size?: SpinnerSize   // default: 'md'
  label?: string       // default: 'Loading…'
}
```

All `SVGAttributes` are forwarded to the `<svg>` element.

---

## 3. Size map

| `size` | Dimensions |
|---|---|
| `xs` | `12×12px` |
| `sm` | `16×16px` |
| `md` (default) | `20×20px` |
| `lg` | `24×24px` |

---

## 4. Wave-N expansion — themeColor (FR-3) + type variants (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (Spinner 30% coverage)
**Rulings applied:** FR-3 (themeColor axis)

### 4.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| No `themeColor` prop | P1 | `themeColor` added — §4.2 |
| No `type`/`variant` surface | P1 | `type` prop added — §4.3 |
| Size enum naming alignment | P2 | Note — §4.4 |

### 4.2 Updated data model

```typescript
// FR-3 themeColor axis — mirrors Loader.Semantic.md §16.3
type SpinnerThemeColor =
  | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error'
  | 'inverse'

// Type variants — mirrors Loader variant vocabulary for the spinner class
type SpinnerType = 'ring' | 'converging'
// ring        — single rotating ring (current implementation; Kendo InfiniteSpinner)
// converging  — dual arcs converging/diverging (Kendo ConvergingSpinner)

interface SpinnerProps extends React.SVGAttributes<SVGElement> {
  size?:       'xs' | 'sm' | 'md' | 'lg'   // default: 'md'; unchanged
  label?:      string                        // default: 'Loading…'; unchanged
  themeColor?: SpinnerThemeColor             // NEW — default: (base/track colour)
  type?:       SpinnerType                   // NEW — default: 'ring'
}
```

### 4.3 themeColor semantics

`themeColor` controls the stroke colour of the spinning arc. When omitted, the spinner uses `--sf-spinner-track-default` (a neutral gray, consistent with the Loader default).

| `themeColor` | Stroke colour | Typical use |
|---|---|---|
| (omitted) | `--sf-spinner-track-default` (neutral) | Inline loading beside text |
| `primary` | Brand primary | Branded loading indicator |
| `secondary` | Brand secondary | Secondary loading contexts |
| `tertiary` | Brand tertiary | Tertiary contexts |
| `info` | Info blue | Information fetch |
| `success` | Success green | Post-success |
| `warning` | Warning amber | Degraded mode |
| `error` | Error red | Error recovery |
| `inverse` | White (inverted) | Dark backgrounds, coloured button loading states |

`themeColor` is orthogonal to `type` and `size`.

### 4.4 type variants

| `type` | Motion description | Kendo equivalent |
|---|---|---|
| `ring` (default) | Single rotating ring | `InfiniteSpinner` |
| `converging` | Dual arcs converging/diverging | `ConvergingSpinner` |

The Spinner `type` vocabulary is a **subset** of Loader `variant` vocabulary — `'dots'` and `'bar'` are not available on Spinner (those are Loader-only). When a host needs dots or bar, use `<Loader variant="dots">` or `<Loader variant="bar">`.

### 4.5 Size naming alignment note

Kendo Loader's size values are `small` / `medium` / `large`; Harborline uses `sm` / `md` / `lg`. Our vocabulary is the FR-3 canonical set (see §3 size map). No migration needed — these were already aligned at spec-authoring time.

The `md` default is `20×20px` in Harborline vs "medium" in Kendo (Kendo doesn't pin px values for Spinner). Functional parity; implementation differs.

### 4.6 Spinner vs Loader relationship post wave-N

After wave-N, the Spinner and Loader overlap in `ring` + `converging` territory. Convention:

- **Spinner** — used where SVG passthrough attributes are needed (`<svg>` root), where the spinner is embedded inside another SVG, or where the host needs a truly inline SVG element.
- **Loader** — used when the spinner sits in a `<div>` layout context (block/inline-flex container) or when `inline`, `label`, or `LoaderOverlay` features are needed.

Implementations MAY share the SVG core; the API surface remains separate.

### 4.7 Deferred from this wave

- `'dots'` and `'bar'` type variants on Spinner — use `<Loader variant>` instead.
- `'converging'` type CSS keyframes — PAO Styling authors the animation.
