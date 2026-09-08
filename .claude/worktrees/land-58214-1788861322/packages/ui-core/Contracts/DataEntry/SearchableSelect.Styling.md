# SearchableSelect — Styling Contract

- **Component:** SearchableSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchableSelect.Semantic.md) · [Interaction](./SearchableSelect.Interaction.md) · [Accessibility](./SearchableSelect.Accessibility.md) · [Styling](./SearchableSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchableSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger button recipe

```
w-full flex items-center justify-between gap-2 px-3 py-2 text-sm rounded-md border bg-white text-left transition-colors
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500
```

Error border: `border-red-500`
Default border: `border-gray-300`

Disabled: `opacity-50 cursor-not-allowed bg-gray-50`
Enabled hover: `hover:border-gray-400 cursor-pointer`

---

## 2. Dropdown panel

```
absolute z-50 mt-1 w-full bg-white border border-gray-200 rounded-md shadow-lg
```

---

## 3. Search input area

```
p-2 border-b border-gray-100
```

Search input:
```
w-full text-sm px-2 py-1.5 rounded border border-gray-200 focus:outline-none focus:ring-2 focus:ring-blue-500
```

---

## 4. Options list

```
max-h-56 overflow-y-auto py-1
```

Option item:
```
flex flex-col px-3 py-2 text-sm cursor-pointer
```

Selected option: `bg-blue-50 text-blue-700`
Unselected hover: `text-gray-900 hover:bg-gray-50`

Group header: `px-3 py-1 text-xs font-semibold text-gray-400 uppercase tracking-wide`

---

## 5. Search highlight

```html
<mark class="bg-yellow-100 text-inherit rounded-sm">
```

---

## 6. Error message

```
text-xs text-red-600
```

---

## 7. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | trigger border | `border-gray-300` |
| hover | trigger border | `hover:border-gray-400` |
| focus | trigger border + ring | `focus:border-blue-500 focus:ring-2 focus:ring-blue-500` |
| error | trigger border | `border-red-500` |
| disabled | trigger | `opacity-50 cursor-not-allowed bg-gray-50` |
| selected option | option bg + text | `bg-blue-50 text-blue-700` |
| search highlight | `<mark>` | `bg-yellow-100 text-inherit rounded-sm` |
