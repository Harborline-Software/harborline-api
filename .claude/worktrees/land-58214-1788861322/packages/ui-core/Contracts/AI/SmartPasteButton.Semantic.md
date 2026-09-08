# SmartPasteButton — Semantic Contract

- **Component:** SmartPasteButton
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./SmartPasteButton.Interaction.md) · [Accessibility](./SmartPasteButton.Accessibility.md) · [Styling](./SmartPasteButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A28 SmartPasteButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SmartPasteButton / KendoReact AI baseline)

---

## 1. Component purpose

**SmartPasteButton** — a button that reads the user's clipboard content, passes it to an AI model, and automatically populates fields in a nearby form. Provides a one-click "smart paste" affordance for structured data entry from unstructured clipboard text (e.g. pasting an address block into separate street/city/zip fields).

---

## 2. Props

```typescript
interface SmartPasteButtonProps {
  onPaste: (clipboardText: string) => Promise<void> | void
  children?: React.ReactNode           // button label; default: "Smart Paste"
  disabled?: boolean
  loading?: boolean                    // caller controls loading state
  className?: string
}
```

The component does NOT call the AI API directly. It reads the clipboard via `navigator.clipboard.readText()` and passes the raw text to `onPaste`. The caller is responsible for invoking the AI model and applying the structured output to form fields.

---

## 3. Clipboard access

Requires `navigator.clipboard.readText()` — a permission-gated API. If the user denies clipboard permission or the API is unavailable (non-HTTPS), the button invokes `onPaste('')` with empty string. The caller should handle the empty-string case gracefully.

---

## 4. Relationship to other AI components

SmartPasteButton is a single-action trigger; it has no output panel. Contrast with AIPrompt (shows inline response) and InlineAIPrompt (expands an input bar). Use SmartPasteButton when the AI output is destined for form fields, not a display panel.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPB1 | High | `navigator.clipboard.readText()` requires `clipboard-read` permission — browsers may show a permission prompt; no error state defined for permission denial | Fix-in-M1: caller should handle the empty `onPaste('')` case and provide feedback |
| G-SPB2 | Medium | No loading feedback built in — `loading` prop exists but the spinner/disabled state during AI processing is caller-managed | Accepted-risk M1; forward-spec baseline |
