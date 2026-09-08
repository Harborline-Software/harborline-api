# SmartPasteButton — Interaction Contract

- **Component:** SmartPasteButton
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SmartPasteButton.Semantic.md) · [Accessibility](./SmartPasteButton.Accessibility.md) · [Styling](./SmartPasteButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A28 SmartPasteButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SmartPasteButton baseline)

---

## 1. Click

Click (or Enter/Space when focused):
1. Calls `navigator.clipboard.readText()`
2. On success: calls `onPaste(clipboardText)`
3. On permission denial or API error: calls `onPaste('')`
4. The button does NOT enter a loading state internally — callers set `loading={true}` while `onPaste` is processing.

When `disabled=true` or `loading=true`: click and keyboard activation are blocked.

---

## 2. Keyboard

Standard button keyboard model: `Enter` and `Space` activate. No additional keyboard behaviors.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPB3 | Medium | No built-in error toast or status announcement on clipboard permission denial — caller receives empty string only | Accepted-risk M1; callers own feedback |
