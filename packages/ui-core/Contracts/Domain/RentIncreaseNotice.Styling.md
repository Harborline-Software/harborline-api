# RentIncreaseNotice — Styling Contract

- **Component:** RentIncreaseNotice
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RentIncreaseNotice.Semantic.md) · [Interaction](./RentIncreaseNotice.Interaction.md) · [Accessibility](./RentIncreaseNotice.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/RentIncreaseNotice.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

`border rounded-lg overflow-hidden` + status border color + `className` passthrough.

---

## 2. Status color map

| Status | Header bg | Border | Text |
| --- | --- | --- | --- |
| `draft` | `bg-gray-50` | `border-gray-200` | `text-gray-600` |
| `sent` | `bg-blue-50` | `border-blue-200` | `text-blue-700` |
| `acknowledged` | `bg-emerald-50` | `border-emerald-200` | `text-emerald-700` |
| `disputed` | `bg-red-50` | `border-red-200` | `text-red-700` |
| `effective` | `bg-purple-50` | `border-purple-200` | `text-purple-700` |

---

## 3. Header band

`px-4 py-3 flex items-start justify-between` + status bg class.

Status badge: `text-xs font-medium px-2 py-0.5 rounded-full border` + status bg + text + border classes.

---

## 4. Rent comparison row

Container: `flex items-center gap-4 mb-4`

Rent card: `flex-1 text-center p-3 bg-gray-50 rounded-lg`

Amount: `text-xl font-bold tabular-nums`

New rent card: adds `border-2 border-gray-200`

Arrow: `text-gray-300 text-lg`

Increase pill:
- Positive: `bg-red-100 text-red-700`
- Zero/negative: `bg-emerald-100 text-emerald-700`
Both: `px-2 py-1 rounded-full text-xs font-bold`

---

## 5. Details grid (`<dl>`)

`grid grid-cols-2 gap-x-4 gap-y-2 text-sm`

Term (`<dt>`): `text-xs text-gray-400`

Value (`<dd>`): `text-right text-xs font-medium text-gray-700`

Monthly increase value: `text-right text-xs font-semibold text-red-600 tabular-nums`

---

## 6. Reason box

`mt-3 p-3 bg-gray-50 rounded-md`

Label: `text-xs text-gray-400 mb-1`

Text: `text-xs text-gray-600`

---

## 7. Action footer

`px-4 py-3 border-t border-gray-100 bg-gray-50 flex gap-2`

Download button: `px-3 py-1.5 text-xs font-medium rounded-md border border-gray-200 bg-white text-gray-700 hover:bg-gray-50 transition-colors`

Acknowledge button: `flex-1 px-3 py-1.5 text-xs font-medium rounded-md bg-emerald-600 text-white hover:bg-emerald-700 transition-colors`

Dispute button: `px-3 py-1.5 text-xs font-medium rounded-md border border-red-200 text-red-600 hover:bg-red-50 transition-colors`
