# Popover — Semantic Contract

- **Component:** Popover
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Popover.Interaction.md) · [Accessibility](./Popover.Accessibility.md) · [Styling](./Popover.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Popover.tsx`
- **Catalog row:** #98 Popover (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-popover`

---

## 1. Component purpose

**Popover** — a composable overlay anchored to a trigger element. A thin re-export wrapper over `@radix-ui/react-popover` with default styling applied to `PopoverContent`.

---

## 2. Exports

| Export | Radix source | Purpose |
|---|---|---|
| `Popover` | `PopoverPrimitive.Root` | State container |
| `PopoverTrigger` | `PopoverPrimitive.Trigger` | Anchor element |
| `PopoverContent` | `PopoverPrimitive.Content` (styled) | Overlay panel |
| `PopoverClose` | `PopoverPrimitive.Close` | Close button primitive |
| `PopoverAnchor` | `PopoverPrimitive.Anchor` | Custom anchor (no trigger button) |

---

## 3. PopoverContent defaults

| Prop | Default |
|---|---|
| `align` | `'center'` |
| `sideOffset` | `6` |

All other Radix `Content` props are forwarded.

---

## 4. Controlled vs. uncontrolled

Inherits Radix behavior: `open`/`onOpenChange` for controlled; `defaultOpen` for uncontrolled.
