# NotificationBell — Semantic Contract

- **Component:** NotificationBell
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NotificationBell.Interaction.md) · [Accessibility](./NotificationBell.Accessibility.md) · [Styling](./NotificationBell.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NotificationBell.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled bell icon with counter badge

---

## 1. Purpose

NotificationBell is a **header-level notification inbox trigger**. It renders
an icon button with an unread-count badge that, when clicked, opens a popover
panel listing recent notifications. Users can mark all notifications as read,
view individual notifications, or navigate to a full notifications page.

---

## 2. Data model

```typescript
interface NotificationBellItem {
  id: string
  title: string
  body?: string
  timestamp: string
  read?: boolean
  onClick?: () => void
}

interface NotificationBellProps {
  items: NotificationBellItem[]
  onMarkAllRead?: () => void
  onViewAll?: () => void
  maxVisible?: number
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `items` | `NotificationBellItem[]` | _required_ | Array of notification items. Empty array = "No notifications" empty state. |
| `onMarkAllRead` | `() => void` | — | When provided and unread count > 0, renders a "Mark all read" action in the panel header. |
| `onViewAll` | `() => void` | — | When provided or `items.length > maxVisible`, renders a "View all" footer link. |
| `maxVisible` | `number` | `5` | Maximum number of items shown in the panel before overflow. |
| `className` | `string` | `''` | Additional classes on the root `<div>`. |

### 3.1 `NotificationBellItem` fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | `string` | Yes | Unique key for React list rendering. |
| `title` | `string` | Yes | Primary notification text. |
| `body` | `string` | No | Optional supporting detail (2-line truncated). |
| `timestamp` | `string` | Yes | Display-formatted time string (not a `Date` — formatted by caller). |
| `read` | `boolean` | No | When falsy, item is styled as unread with a blue dot indicator. |
| `onClick` | `() => void` | No | Called when the notification item is clicked; panel closes after. |

### 3.2 Unread count

Unread count = `items.filter(i => !i.read).length`. Displayed as a red badge
on the bell button. When unread count > 9, badge shows "9+".

### 3.3 Overflow

`items.slice(0, maxVisible)` limits the panel list. If `items.length > maxVisible`,
the "View all (N)" footer link is shown regardless of `onViewAll`.

---

## 4. Events

| Event | Trigger |
| --- | --- |
| `item.onClick()` | Clicking a notification item (panel closes after) |
| `onMarkAllRead()` | "Mark all read" button in panel header |
| `onViewAll()` | "View all" footer link click (panel closes after) |

---

## 5. Composition

```tsx
<NotificationBell
  items={notifications}
  onMarkAllRead={() => markAllRead()}
  onViewAll={() => navigate('/notifications')}
/>
```

---

## 6. Deferred features

- **Real-time updates** — no built-in polling or WebSocket integration;
  host updates `items` prop.
- **Notification grouping** — items are a flat list; no date grouping.
- **Delete / archive individual item** — per-item actions other than
  `onClick` are not supported.
- **Animation** — panel appears instantly; no entrance animation.
