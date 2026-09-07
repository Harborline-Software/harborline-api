# ConnectionStatus — Styling Contract

- **Component:** ConnectionStatus
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConnectionStatus.Semantic.md) · [Interaction](./ConnectionStatus.Interaction.md) · [Accessibility](./ConnectionStatus.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConnectionStatus.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Base: `flex items-center justify-between gap-3 rounded-lg border px-4 py-2.5` + state-specific background/border/text class + `className` passthrough.

---

## 2. State classes

| State | Container (`bg`) | Background class |
| --- | --- | --- |
| `offline` | Red | `bg-red-50 border-red-200 text-red-800` |
| `reconnecting` | Amber | `bg-amber-50 border-amber-200 text-amber-800` |

(`online` renders `null` — no container.)

---

## 3. Status dot

`h-2 w-2 rounded-full shrink-0` + state dot class.

| State | Dot class |
| --- | --- |
| `offline` | `bg-red-500` |
| `reconnecting` | `bg-amber-400 animate-pulse` |

---

## 4. Label text

`text-sm font-medium` (inherits container text color).

Offline hint text: `text-xs opacity-75` — "Your changes will sync when reconnected."

---

## 5. Retry button

`text-xs font-semibold underline hover:no-underline focus:outline-none shrink-0`

Inherits the container's text color (red for offline state).
