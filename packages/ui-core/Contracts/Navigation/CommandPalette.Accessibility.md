# CommandPalette — Accessibility Contract

- **Component:** CommandPalette
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CommandPalette.Semantic.md) · [Interaction](./CommandPalette.Interaction.md) · [Styling](./CommandPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/CommandPalette.tsx`
- **Catalog row:** #A1 CommandPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="dialog"` | Dialog container | Modal dialog landmark |
| `aria-modal="true"` | Dialog container | Signals modal behavior |
| `aria-label="Command palette"` | Dialog container | Accessible name |
| `aria-hidden="true"` | Backdrop div | Hidden from AT |
| `role="combobox"` | Search `<input>` | ARIA combobox pattern |
| `aria-autocomplete="list"` | Search `<input>` | Suggests list-based completion |
| `aria-controls="command-list"` | Search `<input>` | References results list |
| `aria-expanded="true"` | Search `<input>` | Always true while open |
| `role="listbox"` | Results `<ul>` | ARIA listbox |
| `id="command-list"` | Results `<ul>` | Linked from `aria-controls` |
| `role="option"` | Each item `<li>` | ARIA option role |
| `aria-selected={isActive}` | Each item | Active (keyboard-highlighted) state |

---

## 2. Focus management

Focus moves to the search input automatically on open (50ms delay). No focus trap is implemented — background page content is reachable via Tab.

---

## 3. Group headers

Group header `<li>` elements have no ARIA role. They are visual-only separators. AT users will hear them as list items but they have no interactive role.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPAL2 | High | No focus trap — keyboard users can Tab out of the modal | Accepted-risk M1 |
| G-CPAL3 | Medium | `aria-activedescendant` not set on input — AT not informed of active item by ARIA | Accepted-risk M1; visual highlighting via `aria-selected` on item; screen readers reading item on focus is not implemented |
| G-CPAL4 | Low | Group header `<li>` items have no `role="group"` or `aria-label` — group names are not AT-accessible | Accepted-risk M1 |
