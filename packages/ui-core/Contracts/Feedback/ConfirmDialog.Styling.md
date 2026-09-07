# ConfirmDialog — Styling Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Interaction](./ConfirmDialog.Interaction.md) · [Accessibility](./ConfirmDialog.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConfirmDialog.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Overlay / backdrop

`fixed inset-0 z-50 flex items-center justify-center p-4` — full-viewport
fixed container with centered flex.

Backdrop: `fixed inset-0 bg-black/40` — semi-transparent black scrim.

---

## 2. Dialog panel

`relative z-10 w-full max-w-md rounded-lg bg-white p-6 shadow-xl`

- Width: fluid, capped at `max-w-md` (28rem / 448px).
- Background: solid white.
- Elevation: `shadow-xl`.

---

## 3. Danger icon badge

Only rendered when `variant='danger'`:

`mb-4 flex h-10 w-10 items-center justify-center rounded-full bg-red-100`

Icon inside: `h-5 w-5 text-red-600`

---

## 4. Typography

| Element | Classes |
| --- | --- |
| Title `<h2>` | `text-base font-semibold text-gray-900` |
| Description `<p>` | `mt-2 text-sm text-gray-500` |

---

## 5. Button row

Layout: `mt-6 flex justify-end gap-3` — right-aligned, 0.75rem gap.

Cancel button:
`rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-gray-300 disabled:opacity-50`

Confirm button — base:
`inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 disabled:opacity-50`

---

## 6. Confirm button variants

| `variant` | Additional classes |
| --- | --- |
| `default` | `bg-blue-600 text-white hover:bg-blue-700 focus-visible:ring-blue-500` |
| `danger` | `bg-red-600 text-white hover:bg-red-700 focus-visible:ring-red-500` |

---

## 7. Loading spinner

Spinner SVG inside confirm button when `loading=true`:
`h-3.5 w-3.5 animate-spin` with `aria-hidden="true"`.
