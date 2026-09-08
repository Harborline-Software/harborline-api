# BottomNavigation — Interaction Contract

- **Component:** BottomNavigation
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./BottomNavigation.Semantic.md) · [Accessibility](./BottomNavigation.Accessibility.md) · [Styling](./BottomNavigation.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/BottomNavigation.tsx`
- **Catalog row:** #12 BottomNavigation (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
SELECTED(i)
  → click item[j] (not disabled) → SELECTED(j) + onValueChange(j)
  → click item[j] (disabled) → no-op
```

Selection is mutually exclusive — only one item selected at a time. Clicking the already-selected item keeps it selected (no deselection).

---

## 2. Disabled items

Disabled items have the `disabled` HTML attribute. The `onClick` guard also checks `item.disabled` before calling `select()`, preventing double-fire edge cases.

---

## 3. Plain Tab order between items — no arrow-key traversal

Tab moves focus between items; there is no ArrowLeft/ArrowRight navigation. Each item is a separate
focusable `<button>` with no `tabindex` of its own.

This is now a **decided** keyboard model rather than a deferral. The selection state is expressed with
`aria-current="page"` (see [Accessibility](./BottomNavigation.Accessibility.md) §1a), and that pattern
carries no roving-`tabindex` obligation — only the rejected `tablist` pattern would have required
Arrow-key traversal with a single tab stop. Adding arrow-key traversal on its own, without the tab
roles, would take tab stops away from consumers who currently reach every destination with Tab.

Asserted by `BottomNavigation.test.tsx` §19 (no item sets `tabindex`).

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BN1 | Medium | `aria-selected` used on `<button>` elements — not valid per WAI-ARIA; `aria-selected` is only valid on `option`, `row`, `tab`, `treeitem` etc. | **CLOSED (earlier repository ticket #3396)** — now `aria-current="page"` on the selected item |
| G-BN2 | Low | No `role="tablist"` on the `<nav>` container; items lack `role="tab"` — tab pattern not implemented | **CLOSED (earlier repository ticket #3396)** — the tab pattern is deliberately not adopted; §3 above and Accessibility §1a record why |
