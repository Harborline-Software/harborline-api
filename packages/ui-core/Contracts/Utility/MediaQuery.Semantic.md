# MediaQuery — Semantic Contract (stub)

- **Component:** MediaQuery
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U8 MediaQuery (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

MediaQuery is a Telerik/KendoReact utility that provides a React render-prop component wrapping `window.matchMedia`. It has no visual surface. This capability is out of scope for `@harborline-software/ui-react` because responsive breakpoint detection is handled declaratively by Tailwind CSS utility classes (`sm:`, `md:`, `lg:`, etc.) in nearly all cases. When JavaScript-level breakpoint detection is genuinely needed (e.g., conditionally mounting heavy components), use the `@/hooks/useMediaQuery` application-layer hook pattern or the `react-responsive` library directly.
