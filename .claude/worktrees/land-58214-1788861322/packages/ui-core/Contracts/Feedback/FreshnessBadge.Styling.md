# FreshnessBadge — Styling Contract

- **Component:** FreshnessBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FreshnessBadge.Semantic.md) · [Interaction](./FreshnessBadge.Interaction.md) · [Accessibility](./FreshnessBadge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/FreshnessBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`inline-flex items-center gap-1 text-xs` + state color class + `className` passthrough.

---

## 2. State colors

| State | Color class |
| --- | --- |
| Fresh | `text-muted-foreground` (design token) |
| Stale | `text-warning` (design token) |

---

## 3. Warning icon

Rendered only when `stale=true`:
`h-3 w-3` SVG triangle (filled), `aria-hidden="true"`, inherits `text-warning` from container.

---

## 4. Note on design tokens

`text-warning` and `text-muted-foreground` are Tailwind CSS design tokens
defined in the project's theme config, not hardcoded palette classes. Both
must be present in the consuming app's Tailwind configuration.
