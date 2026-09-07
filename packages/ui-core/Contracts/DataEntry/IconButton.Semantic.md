# IconButton — Semantic Contract

- **Component:** IconButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./IconButton.Interaction.md) · [Accessibility](./IconButton.Accessibility.md) · [Styling](./IconButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/IconButton.tsx`
- **Catalog row:** #A29 IconButton (`app-priority: medium`, `library-scope: v1`) — icon-only Button variant; alias of Button #17 with `size="icon"`
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<button>` icon wrapper

---

## 1. Component purpose

**IconButton** — a square button that renders an icon with no visible label text. Requires an `aria-label` for accessibility. Supports multiple visual variants and sizes, and a loading state with spinner.

---

## 2. Props

```typescript
interface IconButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  'aria-label': string                                      // REQUIRED
  variant?: 'default' | 'ghost' | 'outline' | 'destructive'  // default: 'default'
  size?: 'sm' | 'md' | 'lg'                               // default: 'md'
  loading?: boolean                                         // default: false
  children?: React.ReactNode                                // icon content
}
```

`aria-label` is required at the TypeScript type level. All `HTMLButtonElement` attributes are spread onto the `<button>`.

---

## 3. Loading state

When `loading=true`:
- Button is `disabled`
- `aria-busy={true}` is set
- Children are hidden; spinner SVG is rendered instead
- `type="button"` is always set (prevents accidental form submit)

---

## 4. Ref forwarding

`React.forwardRef` — accepts a ref to the underlying `<button>` element.

---

## 5. Icon sizing

Icon size is controlled by the `size` prop via `ICON_SIZE_CLASSES`. The `children` (icon) receives size classes via a wrapper; callers should pass icon SVGs as direct children and let the wrapper size them.
