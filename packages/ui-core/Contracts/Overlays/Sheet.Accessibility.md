# Sheet — Accessibility Contract

- **Component:** Sheet
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sheet.Semantic.md) · [Interaction](./Sheet.Interaction.md) · [Styling](./Sheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A5 Sheet / SidePanel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; shadcn Sheet baseline)

---

## 1. ARIA roles

`SheetContent` renders as `role="dialog"` (Radix Dialog.Content). Required attributes:
- `aria-labelledby` pointing to `SheetTitle` id.
- `aria-describedby` pointing to `SheetDescription` id (when present).

---

## 2. SheetTitle and SheetDescription

`SheetTitle` maps to Radix `Dialog.Title` — provides the accessible name. `SheetDescription` maps to Radix `Dialog.Description` — provides supplemental description. Both must be present or explicitly hidden for AT compliance.

---

## 3. Focus trap

Radix Dialog traps focus within `SheetContent` when open — keyboard users cannot Tab outside.

---

## 4. Escape key

Radix Dialog handles Escape → `onOpenChange(false)`.

---

## 5. Overlay

`SheetOverlay` does not have an ARIA label — it is a visual dismiss mechanism. Screen readers interact with the Sheet via the dialog role.

---

## 6. Known gaps

None identified for forward-spec.
