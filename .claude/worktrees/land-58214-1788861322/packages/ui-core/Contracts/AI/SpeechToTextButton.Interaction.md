# SpeechToTextButton — Interaction Contract

- **Component:** SpeechToTextButton
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SpeechToTextButton.Semantic.md) · [Accessibility](./SpeechToTextButton.Accessibility.md) · [Styling](./SpeechToTextButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A29 SpeechToTextButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SpeechToTextButton baseline)

---

## 1. State machine

```
IDLE (recording=false)
  → click → REQUEST_MIC_PERMISSION → (granted) → RECORDING
                                   → (denied) → IDLE + onError('not-allowed')

RECORDING (recording=true)
  → click → recognition.stop() → IDLE
  → recognition onresult (interim) → onTranscript(text, false)
  → recognition onresult (final) → onTranscript(text, true)
  → recognition onerror → onError(event.error) → IDLE
  → recognition onend (if continuous=false and utterance completes) → IDLE
```

---

## 2. Click to start / stop

Click when IDLE → starts recording. Click when RECORDING → stops recording. Single-click toggle. When `disabled=true` or the API is unavailable: click is blocked.

---

## 3. Continuous mode

When `continuous=true`: recognition continues until explicitly stopped (next click). When `continuous=false` (default): recognition stops after the first utterance ends.

---

## 4. Keyboard

Standard toggle button: `Enter` and `Space` toggle recording state.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-STT3 | Medium | No timeout for silence — if the user stops speaking in continuous mode, recording continues indefinitely | Accepted-risk M1; callers can implement a silence-detection timeout via `onTranscript` timestamps |
