# AIPrompt — Accessibility Contract

- **Component:** AIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AIPrompt.Semantic.md) · [Interaction](./AIPrompt.Interaction.md) · [Styling](./AIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A25 AIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik AIPrompt / KendoReact AI baseline)

---

## 1. Input region

Prompt header: `<h3>` or `role="heading" aria-level="3"`. Textarea: `aria-label="AI prompt input"` or `aria-labelledby` pointing to the header. Send button: `aria-label="Send prompt"` with `aria-disabled` when disabled.

---

## 2. Output region

Output container: `role="region" aria-label="AI response"` with `aria-live="polite"`. During `status: 'pending'`, announces `"Generating response..."`. On completion, announces the full response (AT reads from beginning of live region).

---

## 3. Suggestions

Suggestion chips: `role="button" tabIndex={0}`. Container: `role="group" aria-label="Suggested prompts"`.

---

## 4. Error state

Error container: `role="alert"` to ensure immediate AT announcement.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AIP-A1 | Medium | Long streaming responses cause repeated live-region announcements in some AT | Resolved in wave-N via streaming swap-region pattern (§6.1) |

---

## 6. Wave-N expansion — accessibility additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 6.1 Streaming live-region pattern (resolves G-AIP-A1)

During `streaming={true}`, apply the WAI-ARIA swap-region pattern (same as Chat Accessibility §3):
- The streaming assistant message element is rendered outside the `role="region" aria-live="polite"` response container (or with `aria-live="off"` on the element itself).
- On `streaming` → `false` transition, the complete message is inserted into the live region, triggering a single AT announcement of the full response.
- This eliminates the per-token announcement noise while still announcing the completed response.

### 6.2 Cancel button

Cancel button: `aria-label="Cancel generation"`. During `loading`: `aria-label="Cancel request"`. When both `loading` and `streaming` are `false`, the button is absent from the DOM entirely (not just hidden) to avoid confusing AT users.

### 6.3 Toolbar items

Toolbar container: `role="toolbar" aria-label="Response actions"`. Each toolbar item button: `aria-label="{item.text or item.ariaLabel}"`. When toolbar is empty (no `toolbarItems`), the toolbar container is absent from the DOM.

### 6.4 Commands view

Commands list: `role="listbox" aria-label="Available commands"`. Each command item: `role="option" aria-selected="false"` (no persistent selection; selection is activation). Activating a command is fire-and-done, not a persistent selection state.

### 6.5 Controlled view tabs

Tab bar: `role="tablist" aria-label="AIPrompt views"`. Each tab: `role="tab" aria-selected={isActive} aria-controls="{panelId}"`. Active tab panel: `role="tabpanel" tabIndex={0}`. This is the standard ARIA tabs pattern (W3C APG).

### 6.6 P1 gaps resolved by wave-N spec

| Gap ID | Severity | Description | Resolution |
|---|---|---|---|
| G-AIP-A2 | P1 | No `dir` / RTL label mirroring | Semantic §7.7 (dir prop) |
| G-AIP-A3 | P1 | Tab bar has no `role="tablist"` | §6.5 above |
| G-AIP-A4 | P1 | No Cancel button keyboard accessibility | §6.2 above (Escape in textarea + Cancel button label) |
