# NumberBadge — Styling Contract

- **Component:** NumberBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberBadge.Semantic.md) · [Interaction](./NumberBadge.Interaction.md) · [Accessibility](./NumberBadge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NumberBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base classes

`inline-flex items-center justify-center rounded-full font-semibold leading-none` + variant + size + `className` passthrough.

---

## 2. Variant classes

| `variant` | Classes |
| --- | --- |
| `default` | `bg-gray-500 text-white` |
| `primary` | `bg-blue-600 text-white` |
| `danger` | `bg-red-500 text-white` |
| `warning` | `bg-amber-500 text-white` |

---

## 3. Size classes

| `size` | Classes |
| --- | --- |
| `sm` | `min-w-[1.125rem] h-[1.125rem] px-1 text-[10px]` |
| `md` | `min-w-[1.375rem] h-[1.375rem] px-1.5 text-xs` |

`min-w` ensures circular appearance for single digits while allowing the pill
to grow for multi-digit counts.
