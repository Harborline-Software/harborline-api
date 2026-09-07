# ProgressBar — Styling Contract

- **Component:** ProgressBar
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ProgressBar.Semantic.md) · [Interaction](./ProgressBar.Interaction.md) · [Accessibility](./ProgressBar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ProgressBar.tsx`
- **Catalog row:** #100 ProgressBar (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex` + orientation class + `className` passthrough.

| Orientation | Additional classes |
|---|---|
| `horizontal` | `flex-col gap-1` |
| `vertical` | `flex-col items-start gap-1` |

---

## 2. Track

`relative overflow-hidden rounded-full bg-muted`

| Orientation | Size |
|---|---|
| `horizontal` | `h-2 w-full` |
| `vertical` | `w-2 flex-1` |

---

## 3. Fill

Base: `rounded-full` + trackColor + animation class + inline `width`/`height` style.

**Theme color → fill class:**

| `themeColor` | Fill class |
|---|---|
| `base` | `bg-secondary` |
| `primary` (default) | `bg-primary` |
| `secondary` | `bg-secondary` |
| `tertiary` | `bg-muted` |
| `info` | `bg-blue-500` |
| `success` | `bg-green-500` |
| `warning` | `bg-yellow-500` |
| `error` | `bg-destructive` |

> **M1 note:** `info`/`success`/`warning` use hardcoded palette classes; others use design tokens.

**Animation:**
- `animation=true`, determinate: `transition-all duration-500 ease-in-out`
- `animation=true`, indeterminate: `animate-pulse`
- `animation=false`: no animation class

**Inline style:**
- Horizontal: `{ width: isIndeterminate ? '40%' : '{pct}%' }`
- Vertical (positioned bottom-up): `{ height: isIndeterminate ? '40%' : '{pct}%' }` + `absolute bottom-0 left-0 right-0`

---

## 4. Label text

`text-xs font-medium text-muted-foreground`

| `labelPlacement` | Additional class |
|---|---|
| `start` | rendered above track |
| `center` | `self-center` |
| `end` (default) | `self-end` |

---

## 5. ChunkProgressBar styling

Container: `flex gap-1 items-center` + `className`

Each chunk: `h-2 flex-1 rounded-sm` + filled/unfilled class + animation class

Filled chunks use `trackColor[themeColor]`; unfilled use `bg-muted`.
