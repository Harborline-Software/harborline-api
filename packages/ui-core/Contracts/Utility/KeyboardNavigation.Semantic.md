# KeyboardNavigation — Semantic Contract (stub)

- **Component:** KeyboardNavigation
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U7 KeyboardNavigation (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

KeyboardNavigation is a Telerik/KendoReact headless utility that manages roving `tabIndex` across a set of child elements — the standard pattern for composite widget keyboard navigation (grids, menus, toolbars). It has no UI surface. This capability is out of scope as a standalone export in `@harborline-software/ui-react` because roving tab index is implemented inline within each component that needs it (Grid, Menu, Toolbar, etc.) following the ARIA APG composite widget pattern. A shared hook (`useRovingTabIndex`) may be extracted to a `packages/ui-core/hooks/` internal utility if needed, but it will not be a public API component.

---

## See Also

Keyboard navigation is owned inline by each interactive component. Components with keyboard-navigation specifications:
- [Accordion](../Layout/Accordion.Interaction.md) — ArrowUp/Down/Home/End between panels
- [DataGrid](../DataDisplay/DataGrid.Interaction.md) — arrow-key cell navigation
- [Menu](../Navigation/Menu.Interaction.md) — arrow-key item navigation
- [Dialog](../Overlays/Dialog.Interaction.md) — focus trap
- [TabStrip](../Navigation/TabStrip.Interaction.md) — arrow-key tab switching
