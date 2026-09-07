# Carousel — Accessibility Contract

- **Component:** Carousel
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Carousel.Semantic.md) · [Interaction](./Carousel.Interaction.md) · [Styling](./Carousel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Carousel.tsx`
- **Catalog row:** #22 Carousel (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Slide container `<div>` | No ARIA role in M1 |
| `<button type="button">` | Previous arrow | `aria-label="Previous"` |
| `<button type="button">` | Next arrow | `aria-label="Next"` |
| `<button type="button">` | Each dot | `aria-label="Go to slide N"` (1-based) |
| `disabled` | Arrow buttons | Boolean when at boundary and `!infinite` |

---

## 2. Labeling

Arrow buttons use text character labels (`‹` / `›` for horizontal; `↑` / `↓` for vertical) with `aria-label` overrides. AT announces the aria-label, not the symbol.

Dot buttons use `aria-label="Go to slide {i+1}"` (1-based index).

---

## 3. Live region gap

The carousel does not announce slide changes to AT in M1. Screen reader users navigating via dots or arrows receive no feedback that the view changed.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CAR4 | High | No `aria-roledescription="carousel"` on the outer container; no `role="group"` or `aria-label` on the slide viewport | Accepted-risk M1; functional but AT users have no carousel context |
| G-CAR5 | High | Auto-play doesn't pause on focus — violates WCAG 2.1 SC 2.2.2 | Accepted-risk M1; see Interaction G-CAR1 |
| G-CAR6 | Medium | No live region for slide change announcement | Accepted-risk M1; AT users cannot detect slide changes |
| G-CAR7 | Low | Hidden slides are not `aria-hidden` — off-screen content still exists in the AT tree | Accepted-risk M1 |
