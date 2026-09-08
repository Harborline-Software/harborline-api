# DataQuery — Semantic Contract (stub)

- **Component:** DataQuery
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U2 DataQuery (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

DataQuery is a Telerik/KendoReact headless utility for client-side data operations: filtering, sorting, grouping, and paging arrays of objects using a declarative `DataState` descriptor. It has no UI surface. This capability is out of scope for `@harborline-software/ui-react` because data manipulation is a domain concern handled at the application or API layer, not the component library. For client-side data operations, use TanStack Table's `getCoreRowModel` / `getSortedRowModel` / `getFilteredRowModel` utilities, or handle state transformations directly in the consuming component's state management.
