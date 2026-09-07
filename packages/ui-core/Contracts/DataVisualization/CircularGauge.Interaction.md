# CircularGauge — Interaction Contract

- **Component:** CircularGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./CircularGauge.Semantic.md) · [Accessibility](./CircularGauge.Accessibility.md) · [Styling](./CircularGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A18 CircularGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik CircularGauge baseline)

---

> **Display-only component.** This Interaction contract is intentionally minimal. Gauge components have no user-initiated interactions — they are read-only display components. All runtime value changes are driven by prop updates from the host. This contract records animation timing and known gaps only; the thinness is by design.

## 1. Display only

CircularGauge is read-only. No user interaction in v1. Value updates animate the needle/arc smoothly (CSS transition on the SVG transform).

---

## 2. Animation

Value change triggers a CSS transition on the needle rotation angle. Default duration: 600ms ease-in-out.

> **Implementation note:** Use CSS `transform` property (not SVG `transform` attribute) for needle rotation. SVG `transform` attribute interpolation is unreliable in Firefox. Use `style={{ transform: 'rotate(Ndeg)', transformOrigin: '50% 100%' }}` with explicit `transform-origin` at the needle pivot point.

---

## 3. Known gaps

None identified for forward-spec.
