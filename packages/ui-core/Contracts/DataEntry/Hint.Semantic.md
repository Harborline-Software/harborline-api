# Hint — Semantic Contract (Alias Stub)

- **Component:** Hint
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted (alias stub)
- **Canonical contracts:** [FormField.Semantic](./FormField.Semantic.md) · [FormField.Interaction](./FormField.Interaction.md) · [FormField.Accessibility](./FormField.Accessibility.md) · [FormField.Styling](./FormField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx`
- **Catalog row:** #69 Hint (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (alias — no separate implementation)
- **Foundation:** see FormField — Hint is an alias for the FormField hint slot

---

**Hint** is a catalog alias for the hint/helper-text sub-element of `FormField`. It is not a separately implemented component — FormField renders its own `<p className="text-xs text-gray-500">` hint slot internally, shown when `hint` is set and no `error` is present.

For all design contracts, refer to the **FormField** contracts linked above.
