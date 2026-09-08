# DockManager — Accessibility Contract

- **Component:** DockManager
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DockManager.Semantic.md) · [Interaction](./DockManager.Interaction.md) · [Styling](./DockManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DockManager.tsx`
- **Catalog row:** #44 DockManager (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="tablist"` / `aria-orientation="horizontal"` | Zone tab strip | APG tabs pattern; roving-tabIndex focus |
| `role="tab"` / `aria-selected` / `aria-controls` | Each tab | Active tab `tabIndex=0`, others `-1` |
| `role="tabpanel"` / `aria-labelledby` | Active panel body | Labelled by its tab |
| `aria-grabbed` | Each tab | `true` while pointer-dragged, else `false` |
| `aria-dropeffect="move"` | Zone tab strip (during drag) | Marks the strip as a drop target |
| `aria-dropeffect="move"` | Empty-zone drop affordance | Drop target for an unpopulated zone |
| `aria-haspopup="menu"` / `aria-expanded` / `aria-controls` | "Move to…" button | Opens the keyboard re-dock menu |
| `role="menu"` / `role="menuitem"` | Move menu + zone items | Keyboard re-dock destinations |
| `aria-live="polite"` (sr-only) | Status region | Announces grab / move outcomes |
| `aria-label={`Close ${p.title}`}` | Close button | Accessible name for `×` button |
| `aria-label={`Move ${p.title} to another zone`}` | Move button | Accessible name for the grip control |

The full APG tabs pattern is in place (no non-interactive `<div>` tabs). Drag and
drop carries `aria-grabbed`/`aria-dropeffect`, and an `aria-live` region narrates
the operation so the keyboard re-dock path is fully announced.

---

## 2. Resolved gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DM4 | High | Tabs not keyboard accessible | **Resolved** — `role="tab"` + roving focus (#1414 L3 keyboard work) |
| G-DM5 | High | No tablist/tab/tabpanel ARIA pattern | **Resolved** — full APG tabs pattern |
| G-DM7 | High | Drag-to-redock was mouse-only | **Resolved** — keyboard "Move to…" menu + `aria-live` announcements |

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DM6 | Low | Zone containers have no landmark role or label | Accepted-risk; tablist roles carry the structure |
