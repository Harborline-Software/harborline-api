# Avatar — Semantic Contract

- **Component:** Avatar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Avatar.Interaction.md) · [Accessibility](./Avatar.Accessibility.md) · [Styling](./Avatar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Avatar.tsx`
- **Catalog row:** #8 Avatar (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>/<img>` with initials fallback

---

## 1. Component purpose

User or entity avatar. Renders an image, initials, icon, or default person silhouette in a fixed-size container. Image load errors fall back to initials → icon → silhouette in priority order.

---

## 2. Props

```typescript
type AvatarShape = 'circle' | 'square' | 'rounded'
type AvatarSize = 'small' | 'medium' | 'large'
type AvatarThemeColor =
  | 'base' | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error'

interface AvatarProps {
  src?: string                  // image URL
  alt?: string                  // image alt text; also used as aria-label on the container
  initials?: string             // 1–2 character initials; shown when no image
  icon?: ReactNode              // icon slot; shown when no image and no initials
  shape?: AvatarShape           // default: 'circle'
  size?: AvatarSize             // default: 'medium'
  themeColor?: AvatarThemeColor // background + text color when showing fallback; default: 'base'
  className?: string
}
```

---

## 3. Fallback priority

1. `src` present and loads successfully → show `<img>`.
2. `src` present but load fails (`onError`) → fall through to initials.
3. `initials` present → show up to 2 uppercase characters.
4. `icon` present → show icon slot.
5. Neither → show default person silhouette SVG (40% of container size, 60% opacity).

---

## 4. Initials truncation

`initials` is trimmed to `slice(0, 2)` before display. Pass 1–2 characters for best results. Long strings are not an error — only the first 2 characters render.

---

## 5. Size scale

| Size | Dimensions |
|---|---|
| `small` | 32×32px (`h-8 w-8`), `text-xs` |
| `medium` | 40×40px (`h-10 w-10`), `text-sm` |
| `large` | 56×56px (`h-14 w-14`), `text-base` |
