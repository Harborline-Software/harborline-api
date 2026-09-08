# Wizard — Styling Contract

- **Component:** Wizard
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Wizard.Semantic.md) · [Interaction](./Wizard.Interaction.md) · [Accessibility](./Wizard.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Wizard.tsx`
- **Catalog row:** #150 Wizard (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-wizard-step-active-ring` | Active step ring color (`blue-600`) |
| `--sf-wizard-step-completed-bg` | Completed step fill (`blue-600`) |
| `--sf-wizard-step-pending-bg` | Pending step fill (`gray-100`) |
| `--sf-wizard-connector-active` | Completed connector line (`blue-600`) |
| `--sf-wizard-connector-pending` | Pending connector line (`gray-200`) |
| `--sf-wizard-step-text-completed` | Label under completed step (`gray-500`) |
| `--sf-wizard-step-text-active` | Label under active step (`blue-600 font-medium`) |
| `--sf-wizard-step-text-pending` | Label under pending step (`gray-400`) |

---

## 2. Tailwind class recipes

### 2.1 Step indicator wrapper `<nav>`

`flex items-center w-full mb-8`

### 2.2 Step circle — completed (`i < currentIndex`)

`h-8 w-8 rounded-full bg-blue-600 flex items-center justify-center flex-shrink-0`

Checkmark SVG inside: `h-4 w-4 text-white`

### 2.3 Step circle — active (`i === currentIndex`)

`h-8 w-8 rounded-full ring-2 ring-blue-600 bg-white flex items-center justify-center flex-shrink-0`

Step number text inside: `text-sm font-semibold text-blue-600`

### 2.4 Step circle — pending (`i > currentIndex`)

`h-8 w-8 rounded-full bg-gray-100 flex items-center justify-center flex-shrink-0`

Step number text inside: `text-sm text-gray-400`

### 2.5 Connector line between steps

`flex-1 h-0.5 mx-2`

| State | Additional |
|---|---|
| Left of active/completed (`i < currentIndex`) | `bg-blue-600` |
| Right of active/pending | `bg-gray-200` |

### 2.6 Step label (below circle, optional)

`mt-1 text-xs text-center`

| State | Additional |
|---|---|
| Completed | `text-gray-500` |
| Active | `text-blue-600 font-medium` |
| Pending | `text-gray-400` |

### 2.7 Content area

`flex-1` — no explicit padding; host controls content spacing.

### 2.8 Navigation footer

`flex items-center justify-between pt-4 mt-4 border-t border-gray-200`

### 2.9 Back button

`px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed`

### 2.10 Next / Finish button

`px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed`

---

## 3. Do / Don't

### Do
- Use `flex-shrink-0` on step circles so they never compress on narrow viewports
- Use `ring-2 ring-blue-600` (not `border`) for the active step — border affects layout; ring does not
- Keep `disabled:cursor-not-allowed` on both buttons so the disabled state is obvious

### Don't
- Don't animate connector lines — the fill change is instant on step transition
- Don't make step circles smaller than `h-8 w-8` (32px) — touch target minimum
