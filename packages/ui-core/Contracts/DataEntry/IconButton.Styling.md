# IconButton — Styling Contract

- **Component:** IconButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./IconButton.Semantic.md) · [Interaction](./IconButton.Interaction.md) · [Accessibility](./IconButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/IconButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base

`inline-flex items-center justify-center rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50`

---

## 2. Variants

| Variant | Classes |
|---|---|
| `default` | `bg-white border border-gray-300 text-gray-700 hover:bg-gray-50 shadow-sm` |
| `ghost` | `text-gray-500 hover:bg-gray-100` |
| `outline` | `border border-current` |
| `destructive` | `text-red-600 hover:bg-red-50` |

---

## 3. Sizes (button dimensions)

| Size | Classes |
|---|---|
| `sm` | `h-7 w-7` |
| `md` | `h-8 w-8` |
| `lg` | `h-10 w-10` |

---

## 4. Icon sizes

| Size | Icon wrapper classes |
|---|---|
| `sm` | `h-3.5 w-3.5` |
| `md` | `h-4 w-4` |
| `lg` | `h-5 w-5` |

---

## 5. Loading spinner

Spinner SVG: `animate-spin` + same icon size classes as the size tier. Replaces children visually during loading.

---

## 6. Design token deviation

`default` variant uses hardcoded `gray-300`, `gray-700`, `gray-50`. `ghost` uses `gray-500`, `gray-100`. `destructive` uses `red-600`, `red-50`. M2 will replace with design tokens.
