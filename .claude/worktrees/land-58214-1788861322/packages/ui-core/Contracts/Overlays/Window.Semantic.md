# Window — Semantic Contract (alias redirect)

- **Component:** Window
- **ADR 0017 family:** Overlays (catalog navigation alias)
- **Contract type:** Semantic
- **Status:** Accepted (redirect — canonical contracts in Layout/)
- **Canonical contracts:** [Semantic](../Layout/Window.Semantic.md) · [Interaction](../Layout/Window.Interaction.md) · [Accessibility](../Layout/Window.Accessibility.md) · [Styling](../Layout/Window.Styling.md)
- **Catalog row:** #149 Window (`app-priority: medium`, `library-scope: v1`)

---

This file is an alias redirect. **The authoritative Window contracts live in
[`Layout/Window.*`](../Layout/Window.Semantic.md).**

Window is a floating draggable/resizable overlay. The reference implementation
lives in `packages/ui-react/src/components/layout/` and the canonical contract
set is maintained under `Contracts/Layout/`. This stub exists to prevent dead-end
navigation for readers arriving via the Overlays directory.

See also: [Dialog.Semantic.md](./Dialog.Semantic.md) — the modal-only alternative.
Window adds drag/resize/minimize/maximize to the dialog pattern.
