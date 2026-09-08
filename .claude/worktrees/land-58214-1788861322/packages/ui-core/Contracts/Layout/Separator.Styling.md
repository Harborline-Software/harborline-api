# Separator — Styling Contract

- **Component:** Separator
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Separator.Semantic.md) · [Interaction](./Separator.Interaction.md) · [Accessibility](./Separator.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Separator.tsx`
- **Catalog row:** #A13 Separator (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-separator-color` | Line color (`gray-200`) |
| `--sf-separator-label-text` | Label text color (`gray-500`) |

---

## 2. Tailwind class recipes

### 2.1 Plain horizontal

`h-px w-full bg-gray-200`

### 2.2 Plain vertical

`h-full w-px bg-gray-200`

### 2.3 Labeled variant wrapper

`flex items-center gap-3`

### 2.4 Labeled half-lines

`h-px flex-1 bg-gray-200`

### 2.5 Labeled text span

`shrink-0 text-xs text-gray-500`
