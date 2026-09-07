# FileSaver — Semantic Contract (stub)

- **Component:** FileSaver
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U6 FileSaver (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

FileSaver is a Telerik/KendoReact utility that wraps the browser `<a download>` / `saveAs` pattern for triggering client-side file downloads. It has no UI surface. This capability is out of scope for `@harborline-software/ui-react` because file download triggering is a one-line browser API call (`URL.createObjectURL` + `<a>.click()`) that does not warrant a component library entry. Applications should implement file saves directly or use the `file-saver` npm package if a cross-browser polyfill is needed.
