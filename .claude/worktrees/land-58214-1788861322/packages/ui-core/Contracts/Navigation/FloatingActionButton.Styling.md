# FloatingActionButton — Styling Contract

- **Component:** FloatingActionButton
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingActionButton.Semantic.md) · [Interaction](./FloatingActionButton.Interaction.md) · [Accessibility](./FloatingActionButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/FloatingActionButton.tsx`
- **Catalog row:** #60 FloatingActionButton (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base (always applied)

```
fixed z-50 inline-flex items-center justify-center
rounded-full font-medium shadow-lg transition-all
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
disabled:pointer-events-none disabled:opacity-50
active:scale-95
```

---

## 2. Alignment

| `align` | Classes |
|---|---|
| `top-start` | `top-4 left-4` |
| `top-end` | `top-4 right-4` |
| `bottom-start` | `bottom-4 left-4` |
| `bottom-end` | `bottom-4 right-4` |

---

## 3. Size — compact (icon only, no `text`)

| Size | Classes |
|---|---|
| `small` | `h-10 w-10 text-sm` |
| `medium` | `h-14 w-14 text-base` |
| `large` | `h-16 w-16 text-lg` |

---

## 4. Size — extended (icon + `text` label)

| Size | Classes |
|---|---|
| `small` | `h-9 px-3 text-xs gap-1.5` |
| `medium` | `h-14 px-5 text-sm gap-2` |
| `large` | `h-16 px-6 text-base gap-2.5` |

---

## 5. Theme color

| `themeColor` | Classes |
|---|---|
| `base` | `bg-secondary text-secondary-foreground hover:bg-secondary/80` |
| `primary` | `bg-primary text-primary-foreground hover:bg-primary/90` |
| `secondary` | `bg-secondary text-secondary-foreground hover:bg-secondary/80` |
| `tertiary` | `bg-muted text-foreground hover:bg-muted/80` |
| `info` | `bg-blue-500 text-white hover:bg-blue-600` |
| `success` | `bg-green-500 text-white hover:bg-green-600` |
| `warning` | `bg-yellow-500 text-white hover:bg-yellow-600` |
| `error` | `bg-destructive text-destructive-foreground hover:bg-destructive/90` |

---

## 6. Icon slot

`<span className="shrink-0">` — icon inherits `text-*` size from parent font-size.

---

## 7. Extended text slot

`<span>` with no additional classes — inherits button font size and color.
