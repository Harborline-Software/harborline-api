# CopyButton — Styling Contract

- **Component:** CopyButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CopyButton.Semantic.md) · [Interaction](./CopyButton.Interaction.md) · [Accessibility](./CopyButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/CopyButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Button base

`inline-flex items-center gap-1.5 rounded px-2 py-1 text-xs font-medium transition-colors` + `className` passthrough.

---

## 2. State-dependent colors

| State | Classes |
|---|---|
| Idle (`copied=false`) | `text-gray-500 bg-gray-100 hover:bg-gray-200` |
| Copied (`copied=true`) | `text-green-600 bg-green-50 hover:bg-green-100` |

---

## 3. Icons

Copy icon (idle): SVG `h-3.5 w-3.5 aria-hidden`

Check icon (copied): SVG `h-3.5 w-3.5 aria-hidden`

---

## 4. Design token deviation

All colors are hardcoded (`gray-500`, `gray-100`, `gray-200`, `green-600`, `green-50`, `green-100`). M2 will replace with design tokens.
