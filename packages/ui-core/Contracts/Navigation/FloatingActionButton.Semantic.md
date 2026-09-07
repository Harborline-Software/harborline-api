# FloatingActionButton — Semantic Contract

- **Component:** FloatingActionButton
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FloatingActionButton.Interaction.md) · [Accessibility](./FloatingActionButton.Accessibility.md) · [Styling](./FloatingActionButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/FloatingActionButton.tsx`
- **Catalog row:** #60 FloatingActionButton (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<button>` styled as FAB

---

## 1. Component purpose

**FloatingActionButton** — a fixed-position circular action button that overlays page content. Supports an optional text label (extended FAB), 4 corner alignments, 8 theme colors, and 3 sizes. Primary entry point for a single high-priority action per view.

---

## 2. Props

```typescript
type FabAlign = 'top-start' | 'top-end' | 'bottom-start' | 'bottom-end'

interface FloatingActionButtonProps {
  icon: React.ReactNode            // required; displayed in the button
  text?: string                    // optional; when set, renders an extended FAB (icon + label)
  'aria-label'?: string            // required for compact FABs whose icon is not a string
  align?: FabAlign                 // default: 'bottom-end'
  themeColor?: 'base' | 'primary' | 'secondary' | 'tertiary'
            | 'info' | 'success' | 'warning' | 'error'
                                   // default: 'primary'
  size?: 'small' | 'medium' | 'large'  // default: 'medium'
  disabled?: boolean               // default: false
  onClick?: React.MouseEventHandler<HTMLButtonElement>
  className?: string
}
```

---

## 3. Variants

| Condition | Behaviour |
|---|---|
| `text` absent, string `icon` | Compact circular FAB; icon string supplies the accessible name unless `aria-label` overrides it |
| `text` absent, React-node `icon` | Compact circular FAB; host must supply a descriptive `aria-label` |
| `text` present | Extended FAB; icon + text label; pill shape |

The `isExtended` flag is derived internally as `!!text`.

---

## 4. Alignment

| `align` | Position |
|---|---|
| `top-start` | top-left corner (`top-4 left-4`) |
| `top-end` | top-right corner (`top-4 right-4`) |
| `bottom-start` | bottom-left corner (`bottom-4 left-4`) |
| `bottom-end` | bottom-right corner (`bottom-4 right-4`) |

The button is `fixed` with `z-50`.
