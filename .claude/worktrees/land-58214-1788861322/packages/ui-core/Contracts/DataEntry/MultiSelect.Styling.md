# MultiSelect — Styling Contract

- **Component:** MultiSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelect.Semantic.md) · [Interaction](./MultiSelect.Interaction.md) · [Accessibility](./MultiSelect.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelect.tsx`
- **Catalog row:** #86 MultiSelect (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger div

Base: `flex min-h-[2.25rem] w-full flex-wrap items-center gap-1 rounded-md border bg-white px-2 py-1.5 text-sm cursor-pointer`

States:
- Normal: `border-gray-300 hover:border-gray-400`
- Error: `border-red-400`
- Disabled: `cursor-not-allowed bg-gray-50 opacity-60`
- Open: `ring-2 ring-blue-500 ring-offset-1`

> **M1 note:** Border and ring colors are hardcoded palette values, not design tokens.

---

## 2. Chip

`inline-flex items-center gap-0.5 rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700 ring-1 ring-blue-200`

Chip remove button: `ml-0.5 rounded-full p-0.5 hover:bg-blue-100`

Remove icon: `h-2.5 w-2.5` SVG

---

## 3. Overflow indicator

`text-xs text-gray-500` — "+N more" plain text.

---

## 4. Search input (inside trigger)

`flex-1 min-w-[4rem] border-0 bg-transparent p-0 text-sm outline-none placeholder:text-gray-400`

---

## 5. Popover content

`z-50 max-h-60 w-[var(--radix-popover-trigger-width)] overflow-y-auto rounded-md border border-gray-200 bg-white shadow-md`

`sideOffset={4}`, `align="start"`, `side="bottom"`

---

## 6. Option item

Base: `flex cursor-pointer items-center gap-2.5 px-3 py-2 text-sm hover:bg-gray-50`

Selected: `bg-blue-50`

Disabled: `cursor-not-allowed opacity-50`

---

## 7. Option checkbox

Unchecked: `flex h-4 w-4 shrink-0 items-center justify-center rounded border border-gray-300`

Checked: `border-blue-600 bg-blue-600` + white SVG check `h-3 w-3`

---

## 8. Empty message

`px-3 py-6 text-center text-sm text-gray-500`
