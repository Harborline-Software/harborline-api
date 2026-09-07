# StatusPill — Styling Contract

- **Component:** StatusPill
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StatusPill.Semantic.md) · [Interaction](./StatusPill.Interaction.md) · [Accessibility](./StatusPill.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/StatusPill.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base classes

`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium` + resolved color classes.

---

## 2. Color resolution

Color classes are resolved from `kind` + `value` via the per-kind color maps.
Unknown values fall back to `bg-gray-100 text-gray-700`.

### `glAccountType`

| Value | Classes |
| --- | --- |
| `Asset` | `bg-blue-100 text-blue-700` |
| `Liability` | `bg-purple-100 text-purple-700` |
| `Equity` | `bg-slate-100 text-slate-700` |
| `Revenue` | `bg-green-100 text-green-700` |
| `Expense` | `bg-amber-100 text-amber-800` |

### `occupancyStatus`

| Value | Classes |
| --- | --- |
| `Occupied` | `bg-green-100 text-green-700` |
| `NoticeGiven` | `bg-amber-100 text-amber-800` |
| `Vacant` | `bg-gray-100 text-gray-700` |
| `OffMarket` | `bg-gray-100 border border-gray-300 text-gray-600` |

### `agingBucket`

| Value | Classes |
| --- | --- |
| `NoBalance` | `bg-gray-50 text-gray-400` |
| `Current` | `bg-gray-100 text-gray-600` |
| `Days0To30` | `bg-yellow-100 text-yellow-700` |
| `Days31To60` | `bg-orange-100 text-orange-700` |
| `Days61To90` | `bg-orange-100 text-orange-800` |
| `Days90Plus` | `bg-red-100 text-red-700` |

### `balanceState`

| Value | Classes |
| --- | --- |
| `Balanced` | `bg-green-100 text-green-700` |
| `OutOfBalance` | `bg-red-100 text-red-700` |

### `workOrderStatus`

| Value | Classes |
| --- | --- |
| `Draft` | `bg-blue-100 text-blue-700` |
| `Sent` | `bg-purple-100 text-purple-700` |
| `Accepted` | `bg-indigo-100 text-indigo-700` |
| `Scheduled` | `bg-yellow-100 text-yellow-700` |
| `InProgress` | `bg-orange-100 text-orange-700` |
| `Completed` | `bg-green-100 text-green-700` |
| `OnHold` | `bg-gray-100 text-gray-700` |
| `Cancelled` | `bg-red-100 text-red-700` |

---

## 3. `outlined` modifier

When `outlined=true` and the resolved class string does not already include
`border`: appends `border border-current` to the resolved classes.
