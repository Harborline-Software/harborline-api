# Timeline — Accessibility Contract

- **Component:** Timeline
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Timeline.Semantic.md) · [Interaction](./Timeline.Interaction.md) · [Styling](./Timeline.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Timeline.tsx`
- **Catalog row:** #136 Timeline (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| Implicit `role="list"` | `<ol>` | Ordered list semantics |
| Implicit `role="listitem"` | `<li>` | Each timeline event |
| `aria-hidden="true"` | Icon wrapper `<span>` | Decorative icon |
| `className="sr-only"` | Dot label `<span>` | Provides AT-readable label when no icon shown visually |

Callers can add `aria-label` to the `<ol>` via `...props` spread.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TMLN1 | Low | Connector lines (`<div>`) are visual separators with no `aria-hidden` — AT may encounter them as empty elements | Accepted-risk M1 |
| G-TMLN2 | Low | Alternating mode renders `invisible` placeholder divs — AT may encounter empty invisible content | Accepted-risk M1 |
