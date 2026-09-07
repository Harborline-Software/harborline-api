# ActionMenu — Accessibility Contract

- **Component:** ActionMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionMenu.Semantic.md) · [Interaction](./ActionMenu.Interaction.md) · [Styling](./ActionMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/ActionMenu.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles

Container `<div>`: `aria-haspopup="menu"`, `aria-expanded={open}`.

Dropdown `<div>`: `role="menu"`.

Each item `<button>`: `role="menuitem"`.

Separators `<hr>`: no ARIA role (presentational).

---

## 2. Default trigger

When `trigger` is omitted, the default 3-dot button has `aria-label="More actions"`. The SVG icon has `aria-hidden`.

---

## 3. Keyboard gaps

Arrow-key navigation (`ArrowDown`, `ArrowUp`, `Home`, `End`) is not implemented. Escape-key dismiss is not implemented. These are required per the ARIA Authoring Practices Guide menu button pattern. See G-AM1 and G-AM2 in Interaction contract.

---

## 4. Focus management

Focus does not move into the menu on open (G-AM3). Screen reader users must Tab into menu items manually.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ACTMENU-A1 | Critical | Missing roving tabIndex/focus management inside `role="menu"` — AT users cannot navigate items | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
| G-ACTMENU-A2 | High | `aria-haspopup="menu"` on container div, not on trigger button — incorrect placement | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-ACTMENU-A3 | Low | Disabled items have no `aria-disabled` attribute | Accepted-risk M1 |
