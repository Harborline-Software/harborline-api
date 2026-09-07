# Switch — Styling Contract

- **Component:** Switch
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Switch.Semantic.md) · [Interaction](./Switch.Interaction.md) · [Accessibility](./Switch.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Switch.tsx`
- **Catalog row:** #130 Switch (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Label wrapper

`inline-flex items-center gap-2 cursor-pointer select-none`

Disabled: adds `cursor-not-allowed opacity-50`

`className` passthrough applies to this wrapper.

---

## 2. Track (visual span)

Base: `relative inline-flex shrink-0 items-center rounded-full transition-colors`

Track size:

| size | Class |
|---|---|
| `small` | `w-8 h-4` |
| `medium` | `w-10 h-5` |
| `large` | `w-12 h-6` |

Track color:

| state | Class |
|---|---|
| On | `bg-primary` |
| Off | `bg-muted` |

> **M1 note:** Uses `bg-primary` and `bg-muted` design tokens — not hardcoded colors.

---

## 3. Thumb

Base: `pointer-events-none block rounded-full bg-white shadow-sm transition-transform translate-x-0.5`

Thumb size:

| size | Class |
|---|---|
| `small` | `h-3 w-3` |
| `medium` | `h-4 w-4` |
| `large` | `h-5 w-5` |

Thumb translate-on:

| size | Class |
|---|---|
| `small` | `translate-x-4` |
| `medium` | `translate-x-5` |
| `large` | `translate-x-6` |

---

## 4. Label text

Static label: `text-sm`

Dynamic (on/off label): `text-sm text-muted-foreground`
