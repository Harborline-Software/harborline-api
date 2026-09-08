# InlineAIPrompt — Accessibility Contract

- **Component:** InlineAIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./InlineAIPrompt.Semantic.md) · [Interaction](./InlineAIPrompt.Interaction.md) · [Styling](./InlineAIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A27 InlineAIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik InlineAIPrompt baseline)

---

## 1. Trigger button

`role="button" aria-label="{triggerLabel}" aria-expanded={isOpen}`.

---

## 2. Input

Inline input: `aria-label="AI prompt"`. Same send button pattern as AIPrompt Accessibility §1.

---

## 3. Response

Response container: `role="note" aria-live="polite"`. Same streaming announcement pattern as AIPrompt Accessibility §2 (debounced, only on `complete`).

---

## 3a. Error state

When `output.status === 'error'`, the response panel switches to `role="alert"` (live region with `aria-live="assertive"` semantics) so AT immediately announces the error. The error message text is rendered as the `role="alert"` child content. The close (×) button on the error panel has `aria-label="Dismiss error"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-INLAI-A1 | Medium | Compact response panel has no heading — AT may not distinguish it from surrounding content | Resolved in wave-N: output cards carry `role="region" aria-label` (§5.1) |

---

## 5. Wave-N expansion — accessibility additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 5.1 Output cards

Output panel container: `role="region" aria-label="AI responses"`. Each output card: `role="article" aria-label="AI response {n}"` (1-based counter).

During `streaming={true}` on the last card: apply the WAI-ARIA swap-region pattern (same as Chat Accessibility §3 / AIPrompt Accessibility §6.1). Streaming partial content renders outside the live region; the complete response is inserted on `streaming` → `false`.

For `status: 'error'` on a card: `role="alert"` on the card body (live region with assertive semantics) so AT immediately announces the error. The error replaces the `role="article"` body for that card only.

### 5.2 Output action buttons

Default copy button: `aria-label="Copy response"`. After copy confirmation: `aria-label="Copied"` for 1.5s then reverts. The status change should be announced via an `aria-live="polite"` status region adjacent to the button (not by changing the button label in a live region, which causes double announcement).

Default discard button: `aria-label="Discard response"`.

Custom `outputActions` buttons: each receives `aria-label` from `action.text`.

### 5.3 Commands context menu

`⌘` trigger button: `aria-label="Show commands" aria-haspopup="menu" aria-expanded={isOpen}`. Context menu: `role="menu" aria-label="Commands"`. Each command item: `role="menuitem"`. Navigation: standard ARIA menu pattern (Arrow keys, Enter/Space to activate, Escape to close).

### 5.4 Cancel button

Cancel/stop button: `aria-label="Cancel generation"`. Uses the same positioning slot as the generate button. When cancel replaces the generate button, AT users hear the label change (focus may be on the button) — no additional live region needed.

### 5.5 Popup open/close (FR-2)

Trigger button (collapsed state): `aria-expanded={open}` (maps to the FR-2 `open` prop value). When `open={true}` the popup receives focus on the first focusable element (the input bar). When the popup closes, focus returns to the trigger button.

### 5.6 P1 gaps resolved by wave-N spec

| Gap ID | Severity | Description | Resolution |
|---|---|---|---|
| G-INLAI-A2 | P1 | Output panel has no ARIA landmark — AT cannot navigate to it | §5.1 (role="region") |
| G-INLAI-A3 | P1 | No streaming live-region mitigation | §5.1 (swap-region pattern) |
| G-INLAI-A4 | P1 | Commands menu has no ARIA structure | §5.3 above |
| G-INLAI-A5 | P1 | No `aria-expanded` on trigger button for popup state | §5.5 (FR-2 adoption) |
