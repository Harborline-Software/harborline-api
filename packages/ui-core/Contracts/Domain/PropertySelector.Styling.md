# PropertySelector — Styling Contract

- **Component:** PropertySelector
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted (domain-relocated to Harborline App; NOT `@harborline-software/ui-react`)
- **Companion contracts:** [Semantic](./PropertySelector.Semantic.md) · [Interaction](./PropertySelector.Interaction.md) · [Accessibility](./PropertySelector.Accessibility.md) · [Styling](./PropertySelector.Styling.md)
- **Reference implementation:** the Harborline App's `src/property/PropertySelector.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger button recipe

```
w-full flex items-center gap-3 px-3 py-2 text-sm rounded-md border bg-white text-left
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500
border-gray-300
```

Disabled overlay: `opacity-50 cursor-not-allowed bg-gray-50`

Enabled hover: `hover:border-gray-400 cursor-pointer`

---

## 2. Dropdown panel

```
absolute z-50 mt-1 w-full bg-white border border-gray-200 rounded-md shadow-lg
```

---

## 3. Search input

```
w-full text-sm px-2 py-1.5 rounded border border-gray-200 focus:outline-none focus:ring-2 focus:ring-blue-500
```

Search area border: `p-2 border-b border-gray-100`

---

## 4. Option list

```
max-h-60 overflow-y-auto py-1
```

Option item:
```
flex items-center gap-3 px-3 py-2.5 cursor-pointer
```

Option — selected: `bg-blue-50`
Option — idle hover: `hover:bg-gray-50`

---

## 5. Chevron animation

```
w-4 h-4 text-gray-400 shrink-0 transition-transform
```

Open state: `rotate-180`

---

## 6. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | trigger border | `border-gray-300` |
| hover | trigger border | `hover:border-gray-400` |
| focus | trigger border + ring | `focus:border-blue-500 focus:ring-2 focus:ring-blue-500` |
| disabled | trigger | `opacity-50 cursor-not-allowed bg-gray-50` |
| selected option | option bg | `bg-blue-50` |
| selected option name | option text | `text-blue-700` |
