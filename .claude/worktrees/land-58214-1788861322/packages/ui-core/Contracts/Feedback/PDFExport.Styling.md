# PDFExport — Styling Contract

- **Component:** PDFExport
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFExport.Semantic.md) · [Interaction](./PDFExport.Interaction.md) · [Accessibility](./PDFExport.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFExport.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper

PDFExport renders `<div ref={containerRef}>{children}</div>` with no applied
Tailwind classes. The wrapper is intentionally unstyled — it is a capture
target for `exportToPDF`, not a visual container.

Any print-specific layout or page-margin styling is applied by the host's
`children` content, not by PDFExport.
