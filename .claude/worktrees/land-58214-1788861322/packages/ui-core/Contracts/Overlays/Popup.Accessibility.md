# Popup — Accessibility Contract

- **Component:** Popup
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popup.Semantic.md) · [Interaction](./Popup.Interaction.md) · [Styling](./Popup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Popup.tsx`
- **Catalog row:** #99 Popup (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA role

Popup is a generic container; it applies no ARIA role by default. Callers are responsible for setting appropriate roles on popup content (e.g., `role="menu"`, `role="listbox"`, `role="dialog"`). The `className` passthrough allows callers to add roles via wrapper elements.

---

## 2. Focus management

Popup does not manage focus on open or close. Callers that render interactive content (menus, dropdowns) must manage focus themselves. This is a known gap (see G-PPUP3 in Interaction contract).

---

## 3. Keyboard dismiss

Popup does not handle `Escape` key. Callers that implement dismissible popups must add their own keydown handler. When Popup wraps a `role="menu"` or `role="dialog"`, the caller's Escape handler should call `onOpenChange(false)`.

---

## 4. Screen reader considerations

- Portal renders into `document.body` — content is in the DOM and readable by screen readers when `open=true`.
- No `aria-live` region; no announcement of popup open/close state.
- Content visibility follows DOM presence: screen readers can reach portal content when open.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PPUP-A1 | High | No focus trap for dialog-like content — screen reader users can tab out of popup | Accepted-risk M1; callers must implement for dialog use cases |
| G-PPUP-A2 | Medium | No Escape-key dismiss built in — callers must handle for WCAG 2.1 SC 1.4.13 (Content on Hover or Focus) | Accepted-risk M1 |
| G-PPUP-A3 | Low | Portal position in DOM does not follow reading order — AT users may encounter content out of logical order | Accepted-risk M1 |
