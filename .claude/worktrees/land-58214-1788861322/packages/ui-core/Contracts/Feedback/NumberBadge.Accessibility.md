# NumberBadge — Accessibility Contract

- **Component:** NumberBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberBadge.Semantic.md) · [Interaction](./NumberBadge.Interaction.md) · [Styling](./NumberBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NumberBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="status"` | Badge `<span>` | Polite live region |
| `aria-label` | Badge `<span>` | Custom label (when provided) or `"{count} notification(s)"` |

---

## 2. Live region behavior

`role="status"` is a polite live region. When the `count` changes,
AT announces the new value at the next opportunity. This is appropriate
for count updates that are informational but not urgent.

---

## 3. Overflow label — intentional design

The default `aria-label` uses the raw `count`, not the display-capped string. When `count=150` and `max=99`:
- Visual display: `"99+"`
- Default AT label: `"150 notifications"`

**This divergence is intentional.** Announcing `"99+ notifications"` gives AT users no more information than the sighted users reading "99+", both knowing only "at least 99". Announcing the actual count (`"150 notifications"`) gives AT users more precise information. This meets WCAG 1.3.1 (info conveyed by text): the accessible name carries at least the information the visual display conveys, plus more.

Hosts who prefer consistent announcement can pass a custom `aria-label` using the display string:
```tsx
// Match visual display exactly (less informative but consistent):
<NumberBadge count={count} max={99} aria-label={count > 99 ? "99 or more notifications" : `${count} notifications`} />

// Default (preferred — announces actual count):
<NumberBadge count={count} max={99} />
// aria-label → "150 notifications" when count=150
```

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-NB-A1 | Low | `role="status"` may not re-announce on count update if the badge was already present and the text content changes — depends on AT implementation | Accepted-risk M1 |
| G-NB-A2 | Low | Default aria-label uses `"{count} notification(s)"` — always says "notification" regardless of context. Hosts whose badge counts items that aren't notifications (errors, tasks, etc.) must pass a custom `aria-label`. | Accepted-risk M1; custom `aria-label` prop is the mitigation |
