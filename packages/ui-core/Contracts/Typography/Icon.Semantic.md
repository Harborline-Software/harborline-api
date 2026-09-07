# Icon — Semantic Contract

- **Component:** Icon
- **ADR 0017 family:** Typography
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** #70 Icon (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled SVG icon wrapper

---

## 1. Component purpose

Two icon rendering modes:

- **Icon** — glyph-font icon by name (`data-icon` + `.k-icon` CSS class lookup).
- **SVGIcon** — renders an SVG component inline with standard size/color control.

---

## 2. Props

### Icon

```typescript
type IconSize = 'xsmall' | 'small' | 'medium' | 'large' | 'xlarge' | 'xxlarge'
type IconThemeColor =
  | 'base' | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error' | 'inherit'

interface IconProps {
  name: string                     // icon name (maps to .k-icon CSS class + data-icon attribute)
  size?: IconSize                  // default: 'medium'
  themeColor?: IconThemeColor      // default: 'base'
  className?: string
}
```

### SVGIcon

```typescript
interface SVGIconProps {
  icon: FC<SVGProps<SVGSVGElement>>  // SVG component to render
  size?: IconSize                    // default: 'medium'
  themeColor?: IconThemeColor        // default: 'base'
  className?: string
}
```

---

## 3. Data model

`name` in `Icon` is a CSS-class-based glyph reference. The glyph renders via CSS `content` pseudo-element when the icon font is loaded. No icon registry is built into the component.

`SVGIcon.icon` is a React component — `FC<SVGProps<SVGSVGElement>>` — rendered with `aria-hidden` and `h-full w-full` classes.

---

## 4. Size scale

| Size | Dimensions |
|---|---|
| `xsmall` | 12×12px (`h-3 w-3`) |
| `small` | 16×16px (`h-4 w-4`) |
| `medium` | 20×20px (`h-5 w-5`) |
| `large` | 24×24px (`h-6 w-6`) |
| `xlarge` | 32×32px (`h-8 w-8`) |
| `xxlarge` | 40×40px (`h-10 w-10`) |
