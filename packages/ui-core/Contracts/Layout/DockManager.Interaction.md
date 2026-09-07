# DockManager — Interaction Contract

- **Component:** DockManager
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DockManager.Semantic.md) · [Accessibility](./DockManager.Accessibility.md) · [Styling](./DockManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DockManager.tsx`
- **Catalog row:** #44 DockManager (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Tab activation

Clicking a tab in a zone sets `activeIds[zone] = panel.id`. Only one panel per zone is shown at a time.

---

## 2. Panel close

Clicking the `×` close button (when `closable !== false`):
1. `e.stopPropagation()` prevents the tab click from also activating the panel
2. Removes panel from local `panels` state
3. Fires `onPanelClose(id)`

No active-tab recovery: if the closed panel was active, the zone falls back to `zonePanels[0]` as the active panel.

---

## 3. Drag-to-redock + keyboard re-dock

Panels move between the five zones two ways; both reassign the panel's `dockZone`,
re-order so the moved panel sits last in its destination zone, fire
`onLayoutChange(panels)` with the full new list, and announce the move via a polite
live region.

**Pointer (HTML5 drag-and-drop).** Each tab is its own drag handle (`draggable`).
On `dragStart` the tab sets `aria-grabbed=true`; zone tab strips become drop
targets (`aria-dropeffect="move"`) and show a restrained 2px inset focus-ring edge
while hovered. While a drag is in flight, every currently-*empty* zone reveals a
slim dashed "Drop in …" affordance so a panel can reach all five zones; off-drag
those affordances collapse (the M1 "zone only renders when it has panels" layout
is preserved at rest).

**Keyboard (mouse-free).** Each tab carries a "Move to…" menu button
(`aria-haspopup="menu"`) — reachable in the tab order from the active tab (roving
parity with the × control). It opens a `role="menu"` listing the other four zones;
choosing one re-docks the panel. `Escape` closes the menu.

Re-docking keeps the moved panel active in its destination zone.

### Controlled vs uncontrolled

Uncontrolled by default — the component owns layout and re-renders the new
arrangement. When the parent treats `panels` as controlled (re-feeds it in response
to `onLayoutChange`, e.g. `onLayoutChange={setPanels}`), the prop-sync effect
reflects the parent's list.

---

## 4. Panel close (unchanged)

Active-tab recovery on close is unchanged: closing the active panel falls back to
`zonePanels[0]` (gap G-DM2 retained as accepted-risk).

---

## 5. Resolved gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DM1 | High | No drag-to-dock or drag-between-zones — panel positions static | **Resolved** — pointer drag-to-redock + keyboard "Move to…" menu (§3) |
| G-DM3 | Low | `onLayoutChange` not called on any operation | **Resolved** — fires on every re-dock with the full new panel list (§3) |
| G-DM2 | Medium | Closing the active panel does not auto-activate an adjacent panel | Accepted-risk; falls back to zonePanels[0] |
