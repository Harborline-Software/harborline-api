# ExcelExport — Semantic Contract (stub)

- **Component:** ExcelExport
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U5 ExcelExport (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

ExcelExport is a Telerik/KendoReact utility that serializes Grid data to `.xlsx` format client-side using the `@progress/kendo-file-saver` and `@progress/kendo-ooxml` packages. It has no UI surface. This capability is out of scope for `@harborline-software/ui-react` because spreadsheet export is a domain-specific feature not broadly applicable across the component library. Applications that need Excel export should use `xlsx` (SheetJS) or `exceljs` directly at the application layer, or request the data as a file download from the server API.
