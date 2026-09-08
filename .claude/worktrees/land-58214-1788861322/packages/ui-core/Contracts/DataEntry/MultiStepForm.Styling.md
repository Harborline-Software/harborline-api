# MultiStepForm — Styling Contract

- **Component:** MultiStepForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiStepForm.Semantic.md) · [Interaction](./MultiStepForm.Interaction.md) · [Accessibility](./MultiStepForm.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiStepForm.tsx`
- **Catalog row:** (not-in-catalog) MultiStepForm (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`flex flex-col gap-8`

---

## 2. Step list

`flex items-start gap-0`
Each step `<li>`: `flex items-start` + `flex-1` when not the last step.

---

## 3. Step circle button

Base: `flex h-8 w-8 items-center justify-center rounded-full text-sm font-semibold`
`focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`
`disabled:cursor-default`

| State | Classes |
|---|---|
| Complete | `bg-blue-600 text-white` |
| Current | `border-2 border-blue-600 text-blue-600 bg-white` |
| Future | `border-2 border-gray-300 text-gray-400 bg-white` |

> **M1 note:** Uses hardcoded `blue-600` and `gray-*` Tailwind values, not design system tokens.

---

## 4. Step label

`mt-1 text-center`
Title: `text-xs font-medium` + color:

| State | Color |
|---|---|
| Current | `text-blue-600` |
| Complete | `text-gray-700` |
| Future | `text-gray-400` |

Description: `text-xs text-gray-400`

---

## 5. Connector line

`mx-2 mt-4 h-0.5 flex-1 transition-colors`

| State | Color |
|---|---|
| Complete (step before it is complete) | `bg-blue-600` |
| Not yet complete | `bg-gray-200` |

---

## 6. Content area

`<div>` with no classes — children rendered directly.
