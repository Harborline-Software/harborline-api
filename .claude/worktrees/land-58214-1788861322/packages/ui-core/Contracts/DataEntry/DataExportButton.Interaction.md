# DataExportButton — Interaction Contract

- **Component:** DataExportButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataExportButton.Semantic.md) · [Accessibility](./DataExportButton.Accessibility.md) · [Styling](./DataExportButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DataExportButton.tsx`
- **Catalog row:** #A-DEX DataExportButton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Single-format click

Direct `onExport(fmt)` call. Sets `busy=fmt`. Awaits result. Clears `busy` in `finally` block regardless of success/error.

---

## 2. Multi-format dropdown open/close

Main button click toggles `open`. Click-outside (mousedown on document, outside `containerRef`) closes the dropdown.

---

## 3. Format item click

1. Sets `busy=fmt` immediately.
2. Closes dropdown (`setOpen(false)`).
3. Awaits `onExport(fmt)`.
4. Clears `busy` in `finally`.

Errors from `onExport` are propagated to the caller (not swallowed) — `onExport` can be a `void | Promise<void>` with its own error handling.

---

## 4. Disabled / loading

Button is `disabled` when `disabled=true` or `isLoading=true`. While loading, clicking the button does nothing (native disabled behavior).

---

## 5. Keyboard behavior

Native `<button>` keyboard behavior. No explicit arrow-key navigation in dropdown (G-DEX1).

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DEX1 | High | No arrow-key navigation in multi-format menu — same gap as ActionMenu | Accepted-risk M1 |
| G-DEX2 | High | No Escape-key close for dropdown | Accepted-risk M1 |
