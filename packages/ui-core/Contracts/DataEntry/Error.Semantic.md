# Error — Semantic Contract (Alias Stub)

- **Component:** Error
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted (alias stub)
- **Canonical contracts:** [FormField.Semantic](./FormField.Semantic.md) · [FormField.Interaction](./FormField.Interaction.md) · [FormField.Accessibility](./FormField.Accessibility.md) · [FormField.Styling](./FormField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx`
- **Catalog row:** #52 Error (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (alias — no separate implementation)
- **Foundation:** see FormField — Error is an alias for the FormField error slot

---

**Error** is a catalog alias for the error-display sub-element of `FormField`. It is not a separately implemented component — FormField renders its own `<p role="alert" className="text-xs text-red-600">` error slot internally.

For all design contracts, refer to the **FormField** contracts linked above.

If a standalone error display element is needed outside of FormField, use **ValidationMessage** (#145) instead.
