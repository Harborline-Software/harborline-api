# Tag — Styling Contract

- **Component:** Tag
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tag.Semantic.md) · [Interaction](./Tag.Interaction.md) · [Accessibility](./Tag.Accessibility.md) · [Styling](./Tag.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Tag.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base recipe

```
inline-flex items-center rounded-full font-medium ring-1 ring-inset
```

The `ring-1 ring-inset` provides a soft inset border characteristic of Tag styling (vs. a regular border). This is the visual differentiator from Badge.

---

## 2. Colour recipes (root span)

| `color` | Tailwind classes |
|---|---|
| `gray` | `bg-gray-100 text-gray-700 ring-gray-200` |
| `blue` | `bg-blue-50 text-blue-700 ring-blue-200` |
| `green` | `bg-green-50 text-green-700 ring-green-200` |
| `amber` | `bg-amber-50 text-amber-700 ring-amber-200` |
| `red` | `bg-red-50 text-red-700 ring-red-200` |
| `purple` | `bg-purple-50 text-purple-700 ring-purple-200` |
| `teal` | `bg-teal-50 text-teal-700 ring-teal-200` |

---

## 3. Size recipes

| `size` | Tailwind classes |
|---|---|
| `sm` | `px-2 py-0.5 text-xs gap-1` |
| `md` | `px-2.5 py-1 text-sm gap-1.5` |

---

## 4. Icon size (leading icon wrapper)

| `size` | Icon wrapper classes |
|---|---|
| `sm` | `shrink-0 h-3 w-3` |
| `md` | `shrink-0 h-3.5 w-3.5` |

---

## 5. Remove button recipes

| `color` | Remove button classes |
|---|---|
| `gray` | `hover:bg-gray-200 text-gray-500` |
| `blue` | `hover:bg-blue-100 text-blue-500` |
| `green` | `hover:bg-green-100 text-green-500` |
| `amber` | `hover:bg-amber-100 text-amber-500` |
| `red` | `hover:bg-red-100 text-red-500` |
| `purple` | `hover:bg-purple-100 text-purple-500` |
| `teal` | `hover:bg-teal-100 text-teal-500` |

Base remove button classes (all colours):

```
ml-0.5 rounded-full p-0.5 transition-colors
```

Disabled remove button additional class:

```
cursor-not-allowed
```

---

## 6. Disabled state

When `disabled={true}`, the root span adds:

```
opacity-60
```

---

## 7. Visual states

| State | Root span | Remove button |
|---|---|---|
| Default | Colour classes above | `text-{color}-500` |
| Disabled | `+ opacity-60` | `cursor-not-allowed` |
| Remove hover | unchanged | `hover:bg-{color}-100 text-{color}-500` |

---

## 8. Token surface

No `--sf-tag-*` tokens are defined. Token migration path:

| Proposed token | Current approach |
|---|---|
| `--sf-tag-bg-{color}` | `bg-{color}-50` / `bg-gray-100` |
| `--sf-tag-fg-{color}` | `text-{color}-700` |
| `--sf-tag-ring-{color}` | `ring-{color}-200` |
| `--sf-tag-remove-{color}` | `text-{color}-500` |

---

## 9. Dark mode

The reference implementation uses explicit light-mode Tailwind colours. Dark mode not implemented. Provider themes supply `dark:` overrides (e.g., `dark:bg-blue-900/40 dark:text-blue-100 dark:ring-blue-700`).

---

## 10. Comparison to Badge

| Feature | Tag | Badge |
|---|---|---|
| Border treatment | `ring-1 ring-inset` | `border border-{variant}-200` |
| Colour system | Named hues (`gray`, `blue`) | Semantic variants (`success`, `danger`) |
| Remove affordance | Built-in `onRemove` | None (deferred to Chip) |
| Content prop | `label: string` | `children: ReactNode` |
| Disabled state | `disabled` prop | None |
