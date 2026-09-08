# ConversationList — Accessibility Contract

- **Component:** ConversationList
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConversationList.Semantic.md) · [Interaction](./ConversationList.Interaction.md) · [Styling](./ConversationList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/ConversationList.tsx`

---

## 1. ARIA structure

**Plain list, NOT an ARIA `listbox`/`option` widget** (revised 2026-07-07 — see §4 G-CL3). Each
row exposes multiple independently-focusable controls (select + rename + delete); that is the
Tab-through button-list pattern, not the roving-tabindex single-tab-stop listbox pattern, and
`role="option"` on a container with nested focusable descendants is an ARIA nested-interactive
violation (axe `nested-interactive`, `[serious]`).

| Attribute | Element | Value |
|---|---|---|
| `aria-label={labels.heading}` | `<ul>` (non-empty state) | Names the list from the visible heading |
| `aria-current="true"` | The active row's select `<button>` | Present only for the row matching `activeId`; omitted (not `"false"`) otherwise |
| `aria-label` | Rename/delete buttons | `"{action}: {title}"` — unambiguous target when read out of visual context |
| `aria-label` | Rename input | `labels.rename` |

## 2. Keyboard operability

- Every row's select action is a real `<button>` — native Enter/Space activation, no
  custom key handling required.
- Rename/delete action buttons are real `<button>` elements, always in the DOM (§Interaction
  §6) — reachable by Tab regardless of hover state. `Escape` in the rename input reverts
  without committing (no focus trap).
- No roving-tabindex arrow-key list navigation in v1 — Tab moves through the natural DOM
  order (accepted for a short, resumable list; see Known gaps G-CL2 for a v2 candidate if
  history lists grow long). This was true even when the row carried `role="option"` — the
  listbox ARIA role never matched the actual (Tab-through) interaction model; §4 G-CL3 removes
  the mismatched role rather than building the widget behavior it implied.

## 3. Screen-reader behavior

- The empty state renders as plain text (§Interaction §5), not an empty list — avoids any
  list-with-zero-items announcement ambiguity.
- The active row is conveyed via `aria-current="true"` on its select button (not
  `aria-selected`, which has no meaning outside a `listbox`/`tree`/`grid` widget context).
- Destructive delete is never a single keystroke or click away from firing (§Interaction
  §4) — this protects screen-reader and switch-access users equally, who are most at risk
  from an accidental single-activation delete.

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CL1 | Low | No arrow-key roving-tabindex navigation among rows (Tab-only) | Accepted-risk v1; candidate for v2 if lists commonly exceed ~20 items |
| G-CL2 | Low | No live-region announcement when a row is deleted or renamed (the list re-render is silent to AT) | Accepted-risk v1; candidate follow-up alongside the broader Pilot SR-live-region work (gap-review #18) |
| G-CL3 | Resolved (2026-07-07) | `role="listbox"`/`role="option"`/`aria-selected` were applied without the matching roving-tabindex listbox keyboard model (G-CL1), and `role="option"` containing nested focusable rename/delete buttons was an axe `nested-interactive` `[serious]` violation, red-ing the Storybook a11y gate on `main`. Fixed by removing the listbox/option roles (this component was never a real ARIA listbox) and conveying the active row via `aria-current="true"` on its select button instead. | Fixed — see `ConversationList.tsx` a11y doc comment |
