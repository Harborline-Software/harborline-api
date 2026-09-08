# Slider — Styling Contract

- **Component:** Slider
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Slider.Semantic.md) · [Interaction](./Slider.Interaction.md) · [Accessibility](./Slider.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Slider.tsx`
- **Catalog row:** #119 Slider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative flex flex-col gap-1`

---

## 2. Track wrapper

`relative flex items-center` + `flex-col` when vertical.

Track inner row: `relative w-full flex items-center` + `trackH[size]`

---

## 3. Track layers

| Layer | Classes |
|---|---|
| Background track | `absolute inset-0 rounded-full bg-muted` + `trackH[size]` |
| Fill track (primary color) | `absolute left-0 top-0 bottom-0 rounded-full bg-primary` + `trackH[size]`; `width: ${pct}%` inline |

---

## 4. Track height by size

| Size | Track class |
|---|---|
| `small` | `h-1` |
| `medium` | `h-1.5` |
| `large` | `h-2` |

---

## 5. Native range input (invisible overlay)

`absolute inset-0 w-full opacity-0 cursor-pointer`
`cursor-not-allowed` when disabled.

---

## 6. Custom thumb

`absolute rounded-full bg-primary border-2 border-white shadow-sm pointer-events-none`

Positioned inline: `left: calc(${pct}% - ${offset}px)` where offset = 6 (small) / 8 (medium) / 10 (large).

| Size | Thumb class |
|---|---|
| `small` | `h-3 w-3` |
| `medium` | `h-4 w-4` |
| `large` | `h-5 w-5` |

---

## 7. Tick marks

Container: `relative w-full flex justify-between`
Each tick: `text-xs text-muted-foreground`

Tick labels show numeric values (`min`, intermediate steps, `max`).
