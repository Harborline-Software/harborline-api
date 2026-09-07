# NotificationDot — Styling Contract

- **Component:** NotificationDot
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationDot.Semantic.md) · [Interaction](./NotificationDot.Interaction.md) · [Accessibility](./NotificationDot.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NotificationDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root wrapper

`relative inline-flex` + `className` passthrough.

---

## 2. Dot container

`absolute -right-1 -top-1 flex h-2.5 w-2.5`

---

## 3. Color classes

| `color` | Dot class |
| --- | --- |
| `default` | `bg-gray-500` |
| `primary` | `bg-blue-600` |
| `danger` | `bg-red-500` |
| `warning` | `bg-amber-400` |
| `success` | `bg-green-500` |

---

## 4. Pulse ring

Only rendered when `pulse=true`:
`absolute inline-flex h-full w-full animate-ping rounded-full opacity-75` + color class, `aria-hidden="true"`.

---

## 5. Dot itself

`relative inline-flex h-2.5 w-2.5 rounded-full` + color class.
