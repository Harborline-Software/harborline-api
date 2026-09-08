# MaskedText — Styling Contract

- **Component:** MaskedText
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedText.Semantic.md) · [Interaction](./MaskedText.Interaction.md) · [Accessibility](./MaskedText.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/MaskedText.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`inline-flex items-center gap-1.5 font-mono text-sm` + `className` passthrough.

Monospace font renders the masked dots and visible characters at consistent
width, preventing layout shift on reveal.

---

## 2. Toggle button

`text-gray-400 hover:text-gray-600 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded`

Icon: `h-3.5 w-3.5`, `aria-hidden="true"`.

| State | Icon |
| --- | --- |
| Masked | Eye (show) SVG |
| Revealed | Eye-slash (hide) SVG |
