# NotificationBell — Accessibility Contract

- **Component:** NotificationBell
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationBell.Semantic.md) · [Interaction](./NotificationBell.Interaction.md) · [Styling](./NotificationBell.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NotificationBell.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `aria-label` | Bell `<button>` | `"Notifications"` or `"Notifications, N unread"` when unread count > 0 |
| `aria-expanded` | Bell `<button>` | `true` when panel open, `false` when closed |
| `aria-haspopup="true"` | Bell `<button>` | Indicates the button opens a popup |
| `role="dialog"` | Notification panel `<div>` | Panel treated as a dialog region |
| `aria-label="Notifications"` | Notification panel `<div>` | Names the dialog |
| `aria-label="Unread"` | Unread dot `<span>` | Labels the decorative unread dot (when present) |
| `aria-hidden="true"` | Bell emoji `<span>` | Decorative bell icon |

---

## 2. Dialog focus management — REQUIRED for `role="dialog"` conformance

`role="dialog"` carries strict requirements from WAI-ARIA 1.2 §6.5. The current M1
implementation is non-conforming. **M2 MUST implement all four of these requirements
before shipping the NotificationBell in a v1 release.**

### 2.1 Focus move on open (REQUIRED)

When the panel opens, focus MUST move to the **first interactive element** inside
the dialog — typically the first notification item (or "Mark all read" button if
present). Focus MUST NOT stay on the bell button.

```tsx
// Pattern: after mount (useEffect with ref)
useEffect(() => {
  if (isOpen) {
    firstInteractiveRef.current?.focus()
  }
}, [isOpen])
```

**Why:** Screen reader users with `role="dialog"` expect focus to enter the
dialog immediately. Keeping focus on the trigger means AT users have no indication
the panel opened and must discover it by blind Tab traversal.

**WCAG citation:** SC 2.1.1 Keyboard; WAI-ARIA APG Dialog Pattern.

### 2.2 Focus trap inside dialog (REQUIRED)

Tab and Shift+Tab MUST cycle focus through interactive elements **within** the
panel only. Tab on the last focusable element wraps to the first; Shift+Tab on
the first wraps to the last.

```tsx
// Pattern: trap with keydown handler
onKeyDown={(e) => {
  if (e.key === 'Tab') {
    const focusable = panelRef.current.querySelectorAll(
      'button, [href], input, [tabindex]:not([tabindex="-1"])'
    )
    const first = focusable[0], last = focusable[focusable.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault(); last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault(); first.focus()
    }
  }
}}
```

**Why:** Without a focus trap, Tab exits the panel into page content. Screen
readers in browse mode will read behind the panel as if it weren't open.

**WCAG citation:** SC 2.1.1 Keyboard; WAI-ARIA APG Dialog Pattern §4.

### 2.3 `aria-modal="true"` (REQUIRED)

The panel `<div role="dialog">` MUST carry `aria-modal="true"`. Without it,
NVDA and JAWS enter Browse Mode and expose page content behind the panel to
screen reader navigation.

```tsx
<div role="dialog" aria-label="Notifications" aria-modal="true">
  ...
</div>
```

**Note:** `aria-modal` suppresses background content from JAWS/NVDA's virtual
cursor; it does NOT affect visual rendering. The visual overlay (if any) is
separate. Both are required.

### 2.4 Escape and focus return (REQUIRED)

- Pressing Escape (when focus is inside the dialog) MUST close the panel.
- When the panel closes for ANY reason (Escape, click-outside, item click, "View all"),
  focus MUST return to the bell button that opened the panel.

```tsx
onKeyDown={(e) => {
  if (e.key === 'Escape') { setIsOpen(false); bellRef.current?.focus() }
}}
```

**WCAG citation:** SC 2.1.1 Keyboard; WAI-ARIA APG Dialog Pattern §5.

---

### 2.5 Implementation decision: `role="dialog"` vs `role="menu"` vs `role="listbox"`

NotificationBell currently uses `role="dialog"`. An alternative is `role="menu"` with
`role="menuitem"` on each notification. Comparison:

| | `role="dialog"` | `role="menu"` |
|---|---|---|
| Focus on open | First interactive element | First menuitem |
| Tab behaviour | Trap (wraps) | Tab exits menu (Arrow keys navigate) |
| Escape | Close + return focus | Close + return focus |
| Non-uniform content | ✓ OK (dialog supports mixed content) | ✗ Awkward — "Mark all read" and "View all" aren't menu items |
| Requirement burden | Higher (focus trap, aria-modal) | Lower (Arrow key nav) |

**Decision: retain `role="dialog"`** — the panel has non-uniform content (header, list, footer
with different semantic purposes) that maps awkwardly to `role="menu"`. Implement §2.1–2.4.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-NB4 | Medium | No Escape key handler — keyboard users cannot close the panel | [SPEC-RESOLVED 2026-06-06] §2.4: Escape MUST close panel + return focus to bell button; M1 impl gap — blocks v1 ship |
| G-NB5 | Critical | No focus management on panel open — `role="dialog"` requires focus to move INTO the dialog on open (WAI-ARIA 1.2 §6.5); staying on the bell button means AT users hear nothing and must discover the panel by blind Tab traversal. | [SPEC-RESOLVED 2026-06-06] §2.1: focus MUST move to first interactive element on open; M1 impl gap — blocks v1 ship |
| G-NB6 | Critical | No focus trap inside `role="dialog"` — Tab exits the panel to page content, violating WAI-ARIA dialog pattern and `aria-modal` semantics | [SPEC-RESOLVED 2026-06-06] §2.2: Tab/Shift+Tab MUST trap within panel; M1 impl gap — blocks v1 ship |
| G-NB7 | High | Panel uses `role="dialog"` without `aria-modal="true"` — NVDA/JAWS enter browse mode and can read behind the panel; SR users lose dialog containment | [SPEC-RESOLVED 2026-06-06] §2.3: `aria-modal="true"` REQUIRED; M1 impl gap |
| G-NB8 | Low | Unread count badge has no accessible label — only the bell button's `aria-label` includes the count | Accepted-risk M1 |
