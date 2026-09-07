# SpeechToTextButton — Accessibility Contract

- **Component:** SpeechToTextButton
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SpeechToTextButton.Semantic.md) · [Interaction](./SpeechToTextButton.Interaction.md) · [Styling](./SpeechToTextButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A29 SpeechToTextButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SpeechToTextButton baseline)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="button"` | Root `<button>` | Implicit from native button |
| `aria-pressed={recording}` | Root | Toggle button state: `true` when recording, `false` when idle |
| `aria-label` | Root | `"Start recording"` (idle) / `"Stop recording"` (recording) — updated on state change |
| `aria-disabled={disabled \|\| !apiAvailable}` | Root | When disabled or API unavailable |
| `aria-live="polite"` | Status region (sibling) | Caller-provided; announces transcript updates |

The `aria-label` MUST change with recording state so AT users hear the current action (not just the component name).

---

## 2. Recording state announcement

When recording starts, the label change from `"Start recording"` to `"Stop recording"` is the AT announcement. No additional `aria-live` region is required on the button itself. Callers who display the interim transcript should place it in an `aria-live="polite"` region for real-time AT feedback.

---

## 3. Unavailability

When the Web Speech API is unavailable (`apiAvailable=false`), the button renders as `aria-disabled="true"`. Callers should supplement with a visible tooltip or adjacent text explaining the browser limitation.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-STT-A1 | Medium | Microphone-active visual indicator (pulsing animation) has no AT equivalent — AT users know recording is active only from the `aria-label` change | Accepted-risk M1; `aria-label` change is the AT signal; visual pulse is decorative |
