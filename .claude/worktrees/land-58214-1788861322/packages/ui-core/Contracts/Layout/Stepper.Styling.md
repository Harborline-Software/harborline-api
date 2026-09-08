# Stepper — Styling Contract

- **Component:** Stepper
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Stepper.Semantic.md) · [Interaction](./Stepper.Interaction.md) · [Accessibility](./Stepper.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Stepper.tsx`
- **Catalog rows:** #160 Stepper / #48 Stepper (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. Container

| Orientation | Base classes |
|---|---|
| `horizontal` | `flex items-start` |
| `vertical` | `flex flex-col` |

`className` passthrough applied.

---

## 2. Step indicator circle

`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm font-semibold` + status class.

> **M1 note:** All status colors are hardcoded Tailwind palette classes, not design tokens.

| Status | Classes |
|---|---|
| `pending` | `border-2 border-gray-300 bg-white text-gray-400` |
| `active` | `border-2 border-blue-600 bg-white text-blue-600` |
| `completed` | `border-2 border-blue-600 bg-blue-600 text-white` |
| `error` | `border-2 border-red-500 bg-red-500 text-white` |

---

## 3. Connector line

| Orientation | Completed | Incomplete |
|---|---|---|
| `horizontal` | `h-0.5 flex-1 mx-2 bg-blue-600` | `h-0.5 flex-1 mx-2 bg-gray-200` |
| `vertical` | `mt-1 w-0.5 flex-1 rounded bg-blue-600` | `mt-1 w-0.5 flex-1 rounded bg-gray-200` |

---

## 4. Step label

Base: `text-xs` (horizontal), `text-sm` (vertical) + status label class.

| Status | Label class |
|---|---|
| `pending` | `text-gray-400` |
| `active` | `text-gray-900 font-semibold` |
| `completed` | `text-gray-700` |
| `error` | `text-red-600 font-semibold` |

Description: `mt-0.5 text-xs text-gray-500` (horizontal: `text-gray-400`)

---

## 5. Horizontal step item wrapper

`flex flex-1 flex-col items-center` + spacer `after:flex-1` on non-last items.

---

## 6. Vertical step item wrapper

`flex gap-3` — indicator column + label column side by side.
