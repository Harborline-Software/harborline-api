# ConversationList — Semantic Contract

- **Component:** ConversationList
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ConversationList.Interaction.md) · [Accessibility](./ConversationList.Accessibility.md) · [Styling](./ConversationList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/ConversationList.tsx`
- **Catalog row:** session-history surface for the AI family (Pilot W1, `_shared/design/pilot-ux-gap-review-2026-07-03.md` #1)
- **Foundation:** none — native component, no Kendo/Vaadin counterpart, no Kendo-minimum obligation

---

## 1. Component purpose

**ConversationList** — lists past assistant conversations (title, timestamp, optional
preview), lets the user resume one, start a new one, rename, or delete. Pairs with
`Chat`/`AIPrompt` as the session-history surface for any assistant harness. Carries **no
persistence, encryption, or retention logic** — those are entirely the consumer's concern
(feedback_ui_react_no_domain_components). The component renders the list it is handed and
reports intent via callbacks; the consumer decides where conversations live and how.

## 2. Props

```typescript
export interface ConversationSummary {
  id: string
  title: string
  /** Pre-formatted, already-localized relative/absolute time string. */
  timestamp: string
  /** Optional last-message preview; caller truncates/redacts. */
  preview?: string
}

export interface ConversationListLabels {
  heading?: string              // default: "Conversations"
  newConversation?: string      // default: "New conversation"
  empty?: string                // default: "No conversations yet."
  rename?: string                // default: "Rename"
  delete?: string                // default: "Delete"
  confirmDelete?: string          // default: "Delete"
  cancel?: string                 // default: "Cancel"
  getRowMenuLabel?: (title: string) => string
}

export interface ConversationListProps {
  conversations: ConversationSummary[]
  activeId?: string | null
  onSelect: (id: string) => void
  onNew?: () => void              // omit to hide "New conversation"
  onRename?: (id: string, title: string) => void  // omit to hide rename action
  onDelete?: (id: string) => void                  // omit to hide delete action
  labels?: ConversationListLabels
  className?: string
}
```

## 3. Data model

Fully controlled/uncontrolled-over-data: the component holds no conversation state of its
own beyond the transient rename/delete-confirm mode of a single row. The consumer owns
`conversations[]` ordering (newest-first is the expected reading order but not enforced).

## 4. Events

| Event | Payload | Fires when |
|---|---|---|
| `onSelect` | `id: string` | A row is activated (click, Enter, Space) |
| `onNew` | — | "New conversation" is clicked |
| `onRename` | `(id, title)` | A rename is committed (Enter or blur with a non-empty, changed title) |
| `onDelete` | `id: string` | The inline delete confirm step is accepted |

## 5. Slots

None — single list region with a heading + optional "New conversation" action + rows.
Each row optionally exposes rename/delete action buttons (visually deferred to
hover/focus, always present in the DOM and tab order).

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/ai/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
