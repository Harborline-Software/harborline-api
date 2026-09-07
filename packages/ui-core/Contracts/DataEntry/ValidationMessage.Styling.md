# ValidationMessage — Styling Contract

- **Component:** ValidationMessage
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationMessage.Semantic.md) · [Interaction](./ValidationMessage.Interaction.md) · [Accessibility](./ValidationMessage.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationMessage.tsx`
- **Catalog row:** #145 ValidationMessage (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Paragraph classes

`text-xs text-destructive mt-0.5` + `className` passthrough.

`className` merges via `cn()` — callers can override or extend.

> **M1 note:** Uses `text-destructive` design token, not a hardcoded color.

---

## 2. Null state

Returns `null` when `message` is falsy — no DOM node rendered.
