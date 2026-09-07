# NotificationBell — Styling Contract

- **Component:** NotificationBell
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationBell.Semantic.md) · [Interaction](./NotificationBell.Interaction.md) · [Accessibility](./NotificationBell.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NotificationBell.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

`relative inline-block` + `className` passthrough.

---

## 2. Bell trigger button

`relative h-9 w-9 rounded-lg flex items-center justify-center text-gray-600 hover:bg-gray-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 transition-colors`

Unread badge on bell:
`absolute top-1 right-1 h-4 w-4 rounded-full bg-red-500 text-white text-[10px] font-bold flex items-center justify-center leading-none`

---

## 3. Panel

`absolute right-0 top-11 z-50 w-80 rounded-xl border border-gray-200 bg-white shadow-lg`

Panel header: `flex items-center justify-between px-4 py-3 border-b border-gray-100`

Panel header title: `text-sm font-semibold text-gray-900`

"Mark all read" button: `text-xs text-blue-600 hover:underline focus:outline-none`

---

## 4. Notification items

Base: `w-full text-left px-4 py-3 hover:bg-gray-50 transition-colors focus:outline-none focus-visible:bg-gray-50`

Unread item: adds `bg-blue-50/40`

Unread dot: `mt-1.5 h-2 w-2 rounded-full bg-blue-500 shrink-0`

Title: `text-sm font-medium text-gray-900 truncate`

Body: `text-xs text-gray-500 line-clamp-2`

Timestamp: `text-xs text-gray-400 mt-0.5`

---

## 5. Empty state

`px-4 py-6 text-center text-sm text-gray-500`

---

## 6. Footer "View all"

`w-full text-center text-xs text-blue-600 hover:underline focus:outline-none py-1` inside a `px-4 py-2 border-t border-gray-100` container.
