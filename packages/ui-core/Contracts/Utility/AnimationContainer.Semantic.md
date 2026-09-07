# AnimationContainer — Semantic Contract (stub)

- **Component:** AnimationContainer
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U1 AnimationContainer (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

AnimationContainer is a Telerik/KendoReact infrastructure wrapper that coordinates entrance/exit animations via the React Transition Group API. It has no visual output of its own — it is a behavioral primitive that wraps other components. This capability is out of scope for `@harborline-software/ui-react` because animation coordination is handled via Tailwind `transition-*` / `animate-*` utilities and the CSS `transition` property applied directly to component styling contracts. For orchestrated mount/unmount animations, use Framer Motion or CSS `@keyframes` at the application layer.

---

## See Also

Animation is owned inline by each component's Interaction contract. Components with notable animation specifications:
- [CircularGauge](../DataVisualization/CircularGauge.Interaction.md) — needle rotation CSS transition
- [Skeleton](../Feedback/Skeleton.Interaction.md) — loading shimmer animation
- [Accordion](../Layout/Accordion.Styling.md) — panel expand/collapse transition
- [Carousel](../Layout/Carousel.Interaction.md) — slide transition
