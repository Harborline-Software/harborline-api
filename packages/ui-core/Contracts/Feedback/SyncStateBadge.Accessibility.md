# SyncStateBadge — Accessibility Contract

- **Component:** SyncStateBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SyncStateBadge.Semantic.md) · [Interaction](./SyncStateBadge.Interaction.md) · [Styling](./SyncStateBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/SyncStateBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="status"` | Outer `<span>` | Marks the component as a live region for advisory status updates |
| `aria-hidden="true"` | Dot `<span>` | Decorative indicator dot |

---

## 2. Live region pairing

**`role="status"` has an implicit `aria-live="polite"` per the ARIA spec** — you do NOT also need an explicit `aria-live` attribute when `role="status"` is set. State transitions (e.g., `syncing` → `synced`) will be announced to AT after any currently-speaking content finishes.

**Do NOT add `aria-live="polite"` alongside `role="status"`** — the combination is redundant and in some AT implementations causes double-announcements.

**Error state treatment:** The `error` state represents a condition that may need urgent user attention. The spec recommends switching the outer `<span>` role to `role="alert"` when `state === 'error'`:

```tsx
<span
  role={state === 'error' ? 'alert' : 'status'}
  aria-atomic="true"
  ...
>
```

`role="alert"` has implicit `aria-live="assertive"` and `aria-atomic="true"` — it interrupts AT speech immediately. This is appropriate for error state, where the user needs to know something went wrong even if they are in the middle of reading other content.

**`aria-atomic="true"`** MUST be present on the container so AT reads the full label text when the state changes, not just the changed characters.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-SS1 | Low | M1 implementation does not set `role="status"` — state transitions are not announced to AT | [RESOLVED 2026-06-06] §1 + §2: `role="status"` is now spec'd; `aria-live` is implicit via the role |
| G-SS2 | Low | `error` state uses the same non-urgent pattern as other states | [RESOLVED 2026-06-06] §2: spec requires `role="alert"` when `state === 'error'` for assertive announcement |
