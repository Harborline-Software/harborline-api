# Alert — Semantic Contract

- **Component:** Alert
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** Interaction (PAO) · [Styling](./Alert.Styling.md) · [Accessibility](./Alert.Accessibility.md)
- **Related contracts:** [StatusBanner.Semantic.md](./StatusBanner.Semantic.md) — domain-semantic inline banner (4 types with strict ARIA intent); [Notification.Semantic.md](./Notification.Semantic.md) — transient toast for action feedback.
- **Reference implementation:** `packages/ui-react/src/components/feedback/Alert.tsx`
- **Catalog row:** #A22 Alert (`app-priority: high`, `library-scope: v1`) — shadcn Alert; replaces deprecated CalloutBox
- **Phase:** ADR 0017-A1 Phase M1 (R9 new component)
- **Foundation:** none — hand-rolled `<div>` alert wrapper

---

## 1. Purpose

Alert is the canonical **general-purpose feedback callout** of `@harborline-software/ui-react`.
It renders a persistent, page-anchored message box with four standard
feedback variants: `info`, `success`, `warning`, `error`. An optional title,
optional action slot, and optional close button extend the base pattern.

Alert vs related components:

| Criterion | Alert | StatusBanner | Notification (toast) |
| --- | --- | --- | --- |
| Persistence | Persistent (until dismissed or removed by host) | Persistent | Transient (auto-dismisses) |
| Placement | Inline in page flow | Inline in page flow | Fixed viewport overlay |
| Semantics | General feedback (4 universal variants) | Domain state (4 ERP-specific types) | Action feedback |
| Action slot | Yes | No | No (title only) |
| Typical context | Page-level error, success confirmation, advisory | Record state (draft/gated/etc.) | Save success, delete confirm |

**Primary Harborline use cases:**

- "Invoice submitted successfully" after a form submit (success)
- "You do not have permission to edit this record" (warning)
- "This page will be unavailable during maintenance on Sunday" (info)
- "Failed to load data — please refresh" (error)

---

## 2. Data model

Alert has no internal state except when `closable` is true (host controls
visibility; Alert only surfaces the close event).

```typescript
type AlertVariant = 'info' | 'success' | 'warning' | 'error'

interface AlertProps {
  variant?: AlertVariant
  title?: string
  children: React.ReactNode
  closable?: boolean
  onClose?: () => void
  action?: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'info' \| 'success' \| 'warning' \| 'error'` | `'info'` | Determines color treatment, icon, and ARIA role. |
| `title` | `string` | — | Optional bold heading above the message body. When absent, only `children` is shown. |
| `children` | `ReactNode` | _required_ | Main message content. May be a string, JSX, or a paragraph. |
| `closable` | `boolean` | `false` | Renders a close button (×) in the trailing corner. The host controls visibility — Alert only fires `onClose`. |
| `onClose` | `() => void` | — | Called when the user activates the close button. |
| `action` | `ReactNode` | — | Optional call-to-action rendered below the message body (e.g. a "Retry" or "View details" button/link). |

### 3.1 Variant semantics

| `variant` | Semantic intent | Icon | ARIA role |
| --- | --- | --- | --- |
| `info` | Neutral informational message | ℹ Info circle | `status` (polite) |
| `success` | Positive outcome or confirmation | ✓ Check circle | `status` (polite) |
| `warning` | Advisory — potential issue | ⚠ Triangle | `alert` (assertive) |
| `error` | Failure — action required | ✕ X circle | `alert` (assertive) |

`info` and `success` use `role="status"` (polite live region) — they are
informational and do not require immediate AT announcement.

`warning` and `error` use `role="alert"` (assertive) — they require attention.

### 3.2 `children` as rich content

Unlike StatusBanner's `message: string`, Alert accepts `ReactNode` as `children`.
This allows multi-line messages, links, code snippets, or lists inside the
Alert body — common for error details.

### 3.3 `action` slot

The `action` slot is an optional `ReactNode` rendered below `children` in a
dedicated row. It typically contains a single `<Button>` or anchor link. The
slot is intentionally unstructured to allow host composition.

### 3.4 `closable` and host-controlled visibility

When `closable={true}`, Alert renders a dismiss button (×). Clicking it fires
`onClose`. Alert itself does NOT hide — the host manages visibility via its own
state:

```tsx
const [visible, setVisible] = useState(true)
{visible && <Alert variant="success" closable onClose={() => setVisible(false)}>…</Alert>}
```

This keeps Alert stateless and makes visibility control explicit.

### 3.5 HTML attribute passthrough

`...props` spreads onto the root element. Supports `id` (for `aria-describedby`
from peer elements), `className`, `data-testid`.

---

## 4. Events — semantics

| Event | Signature | Trigger |
| --- | --- | --- |
| `onClose` | `() => void` | User clicks or presses Space/Enter on the close button. |

No other events. Alert does not surface focus or hover events.

---

## 5. Slots

| Slot | Prop | Description |
| --- | --- | --- |
| Main content | `children` | Required. The message body. |
| Action | `action` | Optional. A call-to-action below the message. |

---

## 6. Component composition

### Page-level success after form submit

```tsx
{submitSuccess && (
  <Alert
    variant="success"
    title="Invoice submitted"
    closable
    onClose={() => setSubmitSuccess(false)}
  >
    Invoice INV-2024-001 was submitted and is pending approval.
  </Alert>
)}
```

### Persistent warning with action

```tsx
<Alert
  variant="warning"
  title="Lease expiring soon"
  action={<Button size="sm" variant="outline" onClick={openRenewalForm}>Start renewal</Button>}
>
  The lease for Unit 4B expires in 14 days.
</Alert>
```

### Inline error (API failure)

```tsx
<Alert variant="error" title="Failed to load vendors">
  The vendor list could not be loaded. Please check your connection and{' '}
  <a href="#" className="underline" onClick={retry}>try again</a>.
</Alert>
```

### Info banner (maintenance notice)

```tsx
<Alert variant="info">
  This page will be unavailable for maintenance on Sunday between 2:00 and 4:00 AM UTC.
</Alert>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Icon override** — a `icon?: ReactNode` prop to replace the default icon.
  Hosts may achieve this today by rendering without a title and including the
  icon in `children`.
- **Banner mode** — full-width strip variant that spans the page header (like
  GitHub's deprecation banners). May need a `display="inline" | "banner"` prop.
- **Animated enter/exit** — slide-in when the Alert mounts, slide-out on close.
  Deferred to PAO Styling wave.
- **Multiple actions** — today `action` is a single `ReactNode`; future
  `actions?: ReactNode[]` for two-button CTAs.

---

## 8. Open questions (for council)

1. **Alert vs StatusBanner overlap.** Both are persistent inline banners.
   Alert's `warning` variant and StatusBanner's `warning` type look similar.
   Leaning: keep both — StatusBanner carries strict ERP domain semantics
   (provisional/gated) and specific ARIA role rules; Alert is the general-
   purpose callout for non-domain feedback.
2. **`role="alert"` for success.** Some implementations give `success` variant
   `role="alert"` (assertive) so AT announces the confirmation. Leaning
   `role="status"` (polite) for `success` — users are rarely surprised by a
   positive outcome and assertive announcement is disruptive.
3. **`children` vs `message` string.** Unlike StatusBanner which accepts only
   `string`, Alert accepts `ReactNode`. Is this too permissive? Leaning keep
   `ReactNode` — Alert's primary non-domain use cases include links and
   structured content in the body.
4. **`id` for `aria-describedby`.** Should Alert provide a default `id` if
   none is supplied, so sibling elements can reference it? Leaning no — the
   host supplies `id` when needed via HTML attribute passthrough.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/feedback/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
