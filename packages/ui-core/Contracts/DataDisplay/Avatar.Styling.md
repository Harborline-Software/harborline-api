# Avatar — Styling Contract

- **Component:** Avatar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Avatar.Semantic.md) · [Interaction](./Avatar.Interaction.md) · [Accessibility](./Avatar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Avatar.tsx`
- **Catalog row:** #8 Avatar (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-avatar-bg-base` | Default fallback background (`muted`) |
| `--sf-avatar-text-base` | Default fallback text (`muted-foreground`) |

---

## 2. Tailwind class recipes

### 2.1 Root `<span>` (always)

`inline-flex items-center justify-center shrink-0 overflow-hidden font-medium select-none` + shape + size + (color when no image)

### 2.2 Shape

| Shape | Class |
|---|---|
| `circle` | `rounded-full` |
| `square` | `rounded-none` |
| `rounded` | `rounded-md` |

### 2.3 Size

| Size | Class |
|---|---|
| `small` | `h-8 w-8 text-xs` |
| `medium` | `h-10 w-10 text-sm` |
| `large` | `h-14 w-14 text-base` |

### 2.4 Theme color (applied when fallback is showing)

| `themeColor` | Class |
|---|---|
| `base` | `bg-muted text-muted-foreground` |
| `primary` | `bg-primary text-primary-foreground` |
| `info` | `bg-blue-100 text-blue-700` |
| `success` | `bg-green-100 text-green-700` |
| `warning` | `bg-yellow-100 text-yellow-700` |
| `error` | `bg-red-100 text-red-700` |

### 2.5 Image `<img>`

`h-full w-full object-cover`

### 2.6 Initials `<span>`

`uppercase leading-none`

### 2.7 Default silhouette SVG

`h-[60%] w-[60%] fill-current opacity-60`
