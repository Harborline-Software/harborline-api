# Ripple — Semantic Contract (stub)

- **Component:** Ripple
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U9 Ripple (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

Ripple is a Telerik/KendoReact decorator component that adds a Material Design ink-ripple touch/click animation to its child element. It has no intrinsic UI. This capability is out of scope for `@harborline-software/ui-react` because the design system uses Tailwind's `active:scale-[0.97]` / `active:brightness-90` CSS utilities for press-state feedback, which is consistent with the shadcn/Radix aesthetic baseline. A full canvas-ripple animation would conflict with the established visual language and adds CSS complexity without design-system approval. If a ripple effect is ever required for a specific component, it will be spec'd in that component's Interaction contract rather than as a shared utility.

**See also:** `active:scale-[0.97] active:brightness-90` Tailwind utilities (the canonical press-state feedback for interactive elements in this design system) — used by [Button.Semantic.md](../DataEntry/Button.Semantic.md) and other interactive components. No shared Ripple utility will be created.
