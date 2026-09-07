# Timeline — Semantic Contract

- **Component:** Timeline
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Timeline.Interaction.md) · [Accessibility](./Timeline.Accessibility.md) · [Styling](./Timeline.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Timeline.tsx`
- **Catalog row:** #136 Timeline (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled vertical timeline

---

## 1. Component purpose

**Timeline** — renders a series of chronological events as a vertical or horizontal timeline with colored dots, connector lines, and optional icons. Supports alternating layout for a magazine-style presentation.

---

## 2. Props

```typescript
interface TimelineItem {
  id: string
  label: string
  description?: string
  date?: string
  icon?: React.ReactNode
  color?: 'default' | 'success' | 'warning' | 'error' | 'info'
}

interface TimelineProps extends React.HTMLAttributes<HTMLOListElement> {
  items: TimelineItem[]
  orientation?: 'vertical' | 'horizontal'  // default: 'vertical'
  alternating?: boolean                     // default: false
}
```

All `React.HTMLAttributes<HTMLOListElement>` (className, aria-*, etc.) are spread onto the `<ol>`.

---

## 3. Color mapping

| color | Dot class |
|---|---|
| `default` | `bg-blue-600 border-blue-600` |
| `success` | `bg-green-600 border-green-600` |
| `warning` | `bg-amber-500 border-amber-500` |
| `error` | `bg-red-600 border-red-600` |
| `info` | `bg-sky-500 border-sky-500` |

---

## 4. Icon slot

When `icon` is provided, it renders inside the dot with `aria-hidden="true"` on the wrapper span; the item's label is in an `<span className="sr-only">` for AT. When no icon, the `<span className="sr-only">` provides the AT label inside the dot.

---

## 5. Alternating layout

When `alternating=true` (vertical only), even-indexed items render content on the right, odd-indexed on the left. The unused side renders an `invisible` placeholder div to maintain grid alignment.

---

## 6. HTML structure

`<ol>` + `<li>` — correct list semantics for a series of events.
