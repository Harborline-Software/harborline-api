# DateMath — Semantic Contract (stub)

- **Component:** DateMath
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U3 DateMath (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

DateMath is a Telerik/KendoReact utility module providing date arithmetic functions (add, subtract, compare, ceiling, floor) used internally by DatePicker, Scheduler, and Calendar components. It has no UI surface. This capability is out of scope for `@harborline-software/ui-react` as a standalone export — date arithmetic is a well-solved problem covered by `date-fns` (the fleet's canonical date library per ADR 0017 M0 conventions). Consuming components in this library use `date-fns` internally; there is no need for a parallel date math module.
