# Tag — Semantic Contract

- **Component:** Tag
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Tag.Interaction.md) · [Accessibility](./Tag.Accessibility.md) · [Styling](./Tag.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Tag.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<span>` tag badge

---

## 1. Purpose

Tag is a compact inline label with optional leading icon and optional remove button. It is used for categorization tags on entities (e.g., property tags, contact labels, work order categories). Tags are typically user-created or assigned and may be dismissible.

Tag differs from Badge in two key ways:

1. **`label` prop** (not `children`). Badge accepts arbitrary ReactNode children; Tag requires a string `label`.
2. **Remove affordance.** Tag has an optional built-in `onRemove` button with colour-matched styling. Badge has no dismiss mechanism.
3. **Colour system.** Tag uses named hue colours (`gray`, `blue`, etc.); Badge uses semantic variant tokens (`success`, `warning`, etc.).

Tag uses a ring border (not a solid border) for its outline treatment — `ring-1 ring-inset`.

---

## 2. Data model

```typescript
type TagColor = 'gray' | 'blue' | 'green' | 'amber' | 'red' | 'purple' | 'teal'
type TagSize = 'sm' | 'md'

interface TagProps extends Omit<React.HTMLAttributes<HTMLSpanElement>, 'children'> {
  label: string
  color?: TagColor
  size?: TagSize
  onRemove?: () => void
  disabled?: boolean
  icon?: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `label` | `string` | _required_ | The tag text. Rendered as visible content AND used as the remove button's accessible name suffix. |
| `color` | `TagColor` | `'gray'` | The hue family: `gray`, `blue`, `green`, `amber`, `red`, `purple`, `teal`. |
| `size` | `TagSize` | `'sm'` | Two sizes: `sm` (text-xs, px-2) and `md` (text-sm, px-2.5). |
| `onRemove` | `() => void` | `undefined` | When provided, renders an `×` remove button. When absent, Tag is non-interactive. |
| `disabled` | `boolean` | `false` | When `true`, the remove button is disabled (cannot fire) and the tag is visually muted (`opacity-60`). |
| `icon` | `ReactNode` | `undefined` | Optional leading icon node. Rendered in an `aria-hidden` span at the appropriate icon size. |
| `className` | `string` | — | Additional classes merged onto the root `<span>`. |
| `...props` | `HTMLAttributes<HTMLSpanElement>` (minus `children`) | — | Spread onto the root `<span>`. |

### 3.1 Remove semantics

When `onRemove` is provided and `disabled` is `false`:
- An inline `<button>` with `aria-label="Remove {label}"` is rendered.
- `e.stopPropagation()` prevents the click from bubbling to parent interactive elements.
- `disabled` check prevents firing the callback when `disabled: true`.

### 3.2 `disabled` semantics

`disabled: true` means the user cannot remove the tag. The tag still renders with its full colour treatment (not greyed on the label). Only the opacity reduces (`opacity-60`). The remove button renders but is `disabled`.

### 3.3 Icon prop

The `icon` prop accepts any ReactNode (typically a small icon component). It is wrapped in an `aria-hidden` span — the `label` provides the accessible content. Icon size follows the `size` prop (`h-3 w-3` for `sm`, `h-3.5 w-3.5` for `md`).

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onRemove` | `() => void` | User clicks the remove button AND `disabled !== true` |

---

## 5. Slots

| Slot prop | Purpose |
|---|---|
| `icon` | Leading icon before label text. Decorative; `aria-hidden`. |

---

## 6. Component composition

- **Entity tag lists.** Multiple Tag components in a `flex-wrap gap-1` container listing a property's category tags.
- **ColorDot + Tag.** `icon={<ColorDot color="blue" size="xs" />}` renders a colour swatch prefix in the tag.
- **FilterBar chips.** Tag used as a visual primitive inside FilterBar chip wrappers.
- **Search result row.** Tag rows listing tags associated with a search result.

---

## 7. Deferred features

- **Clickable tag (filter toggle)** — Tag without `onRemove` but with `onClick`. Deferred; use `...props` passthrough or compose inside `<button>`.
- **Max length enforcement** — dev-mode warning for long labels. Deferred.
- **Tag colour auto-assignment** — deterministic colour from label hash. Deferred; host assigns colour.
