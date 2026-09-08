# Icon — Semantic Contract

- **Component:** Icon
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled SVG icon wrapper

---

## 1. Purpose

`Icon.tsx` exports two components:

- **`Icon`** — renders a Kendo/Telerik icon by name using the `k-icon` CSS class convention and `data-icon` attribute. The icon glyph is rendered via CSS (e.g., font icon or content injection). Adds `role="img"` + `aria-label={name}` for accessibility.
- **`SVGIcon`** — renders an inline SVG component (a React functional component accepting `SVGProps<SVGSVGElement>`). The SVG is decorative (`aria-hidden`) and the wrapper `<span>` has no role. SVGIcon is used when the icon is purely decorative (the surrounding context provides the accessible label).

---

## 2. Data model

### `Icon`

```typescript
type IconSize = 'xsmall' | 'small' | 'medium' | 'large' | 'xlarge' | 'xxlarge'
type IconThemeColor =
  | 'base' | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error' | 'inherit'

interface IconProps {
  name: string
  size?: IconSize
  themeColor?: IconThemeColor
  className?: string
}
```

### `SVGIcon`

```typescript
interface SVGIconProps {
  icon: React.FC<React.SVGProps<SVGSVGElement>>
  size?: IconSize
  themeColor?: IconThemeColor
  className?: string
}
```

---

## 3. Props — `Icon`

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `name` | `string` | _required_ | The icon name. Used as the CSS icon identifier (`data-icon={name}`) and as the `aria-label`. Must be a valid icon name in the Kendo/Telerik icon font. |
| `size` | `IconSize` | `'medium'` | Physical size of the icon: xsmall (12px) → xxlarge (40px). |
| `themeColor` | `IconThemeColor` | `'base'` | Semantic colour role: `base`=foreground, `primary`=brand, `info`/`success`/`warning`/`error`=status, `inherit`=inherits parent colour, etc. |
| `className` | `string` | `undefined` | Additional classes merged via `cn()`. |

### 3.1 Icon rendering mechanism

`Icon` renders a `<span class="k-icon ...">` with `data-icon={name}`. The actual glyph is rendered by an external CSS class (Kendo icon font or CSS content injection). The React component does not embed SVG paths — it relies on the icon font being loaded in the environment.

### 3.2 `SVGIcon` props

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `icon` | `React.FC<React.SVGProps<SVGSVGElement>>` | _required_ | The SVG icon component (e.g., a lucide-react icon). |
| `size` | `IconSize` | `'medium'` | Same size axis as `Icon`. |
| `themeColor` | `IconThemeColor` | `'base'` | Same colour axis as `Icon`. |
| `className` | `string` | `undefined` | Additional classes on the wrapper span. |

---

## 4. Events

Both `Icon` and `SVGIcon` are non-interactive and emit no callbacks.

---

## 5. Slots

Neither component has slot props.

---

## 6. Component composition

- **Button label icons.** `SVGIcon` is used with lucide-react icons as leading/trailing decorations inside Button. The button's text provides the accessible name; the icon is decorative.
- **Status indicators.** `Icon` with `themeColor="success"` or `themeColor="error"` in DataGrid cells or form fields.
- **Navigation items.** `Icon` in SideNav items; the nav text label provides the accessible name.
- **Toolbar actions.** `SVGIcon` in icon-only toolbar buttons, where the button's `aria-label` provides the accessible name.

---

## 7. Deferred features

- **Icon catalog enumeration** — a TypeScript union type for all valid `name` values. Currently `name: string` (unconstrained). Deferred; depends on the icon font's catalog.
- **Sprite-based SVG** — `Icon` as a `<use>` reference into an SVG sprite. Deferred.
- **Size numeric override** — passing a pixel value directly instead of the named size. Deferred; use `className` override.
