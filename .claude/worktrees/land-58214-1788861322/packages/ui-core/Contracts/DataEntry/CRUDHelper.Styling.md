# CRUDHelper / DataForm — Styling Contract

- **Component:** CRUDHelper / DataForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CRUDHelper.Semantic.md) · [Interaction](./CRUDHelper.Interaction.md) · [Accessibility](./CRUDHelper.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 CRUDHelper / DataForm (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin CRUD baseline)

---

## 1. Split layout container

`flex h-full gap-0 border border-border rounded-md overflow-hidden`

---

## 2. Grid pane

`flex-1 overflow-hidden border-r border-border`

Toolbar area (above grid): `flex items-center justify-between px-4 py-2 border-b border-border`

"New item" button: `Button variant="outline" size="sm"`

---

## 3. Form pane

`w-[380px] shrink-0 flex flex-col bg-background`

Form header: `flex items-center justify-between px-4 py-3 border-b border-border text-sm font-semibold`

Form body: `flex-1 overflow-y-auto px-4 py-4`

Form footer: `flex items-center gap-2 px-4 py-3 border-t border-border`

Save button: `Button variant="default"`, Cancel button: `Button variant="ghost"`

---

## 4. Modal layout

Grid is full-width. Form renders inside a `Dialog` (follows Dialog styling contract).

---

## 5. Design tokens

Uses design tokens: `border-border`, `bg-background`. No hardcoded palette colors.
