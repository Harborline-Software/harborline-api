# ConversationList — Interaction Contract

- **Component:** ConversationList
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConversationList.Semantic.md) · [Accessibility](./ConversationList.Accessibility.md) · [Styling](./ConversationList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/ConversationList.tsx`

---

## 1. Row selection

Click, `Enter`, or `Space` on a row's main button fires `onSelect(id)`. The active row
(`id === activeId`) is styled distinctly and carries `aria-current="true"` on its select
button (§Accessibility) but does not prevent re-selecting itself — `onSelect` fires again
(idempotent for the consumer).

## 2. New conversation

Clicking "New conversation" fires `onNew()`. Hidden entirely when `onNew` is omitted (no
disabled ghost button — an omitted callback means the action does not exist for this
consumer, not "temporarily unavailable").

## 3. Rename (two-step: click → inline edit → commit)

1. Click the row's rename (✎) button → the row switches to an inline text input,
   pre-filled and selected.
2. `Enter` or blur commits: if the trimmed value is non-empty and differs from the
   current title, fires `onRename(id, trimmedTitle)`; otherwise reverts silently.
3. `Escape` reverts the draft and returns to view mode without committing.

Only one row may be in rename mode at a time (each row owns its own local mode; opening
rename on a second row does not auto-cancel the first in v1 — acceptable because the
first row's `onBlur` fires on focus-out, committing or reverting it before the second
row's mode is set).

## 4. Delete (two-step: click → inline confirm → commit)

1. Click the row's delete (✕) button → the row switches to an inline confirm
   (title reminder + Delete/Cancel buttons). **No destructive action fires on the
   first click.**
2. Confirm → fires `onDelete(id)`, returns the row to view mode (the consumer is
   expected to remove it from `conversations[]`).
3. Cancel → returns to view mode with no side effect.

## 5. Empty state

When `conversations.length === 0`, the list renders `labels.empty` centered in the list
region instead of a `role="listbox"` (an empty listbox with no options is a degenerate
a11y case; the plain-text empty state is clearer for both sighted and screen-reader
users). "New conversation" (if present) remains visible in the header.

## 6. Row action visibility

Rename/delete buttons are visually deferred (`opacity-0` → `group-hover`/`focus-within`
reveals them) but are **always in the DOM and tab order** — a keyboard-only user reaches
them via Tab without needing to hover (§Accessibility G-CL1 disposition: not a gap, this
is the intended pattern, matching `NotificationCenter`'s dismiss-button precedent).
