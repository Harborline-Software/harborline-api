# SpeechToTextButton — Semantic Contract

- **Component:** SpeechToTextButton
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./SpeechToTextButton.Interaction.md) · [Accessibility](./SpeechToTextButton.Accessibility.md) · [Styling](./SpeechToTextButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A29 SpeechToTextButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SpeechToTextButton / Web Speech API baseline)

---

## 1. Component purpose

**SpeechToTextButton** — a toggle button that activates the browser's Web Speech API to transcribe speech in real-time. The transcription is delivered to the caller via `onTranscript`. Typically composed alongside a text input to enable voice-driven input.

---

## 2. Props

```typescript
interface SpeechToTextButtonProps {
  onTranscript: (text: string, isFinal: boolean) => void
  onError?: (error: SpeechRecognitionError) => void
  lang?: string                       // BCP 47 language tag; default: browser default
  continuous?: boolean                // default: false — single utterance vs continuous
  disabled?: boolean
  className?: string
}
```

`isFinal: false` indicates an interim transcript update (live preview); `isFinal: true` indicates a completed utterance.

The component does NOT manage the input field. The caller wires `onTranscript` to update the form field value.

---

## 3. Browser compatibility

Web Speech API (`SpeechRecognition` / `webkitSpeechRecognition`) is supported in Chrome/Edge but not Firefox or Safari as of the forward-spec date. The button renders as `disabled` when the API is unavailable. The caller should render a fallback affordance for unsupported browsers.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-STT1 | High | Web Speech API is not available in Firefox and Safari — component is non-functional in those browsers; no polyfill path defined | Accepted-risk M1; browser-limitation; document in UI and caller notes |
| G-STT2 | Medium | Microphone permission must be granted by the user — no user-facing permission request flow defined; `onError` receives the `not-allowed` error | Fix-in-M1: document error handling pattern for permission denial |
