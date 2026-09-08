# InlineEdit — Styling Contract

- **Component:** InlineEdit
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./InlineEdit.Semantic.md) · [Interaction](./InlineEdit.Interaction.md) · [Accessibility](./InlineEdit.Accessibility.md) · [Styling](./InlineEdit.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/InlineEdit.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. View mode button recipe

```
group flex w-full items-center gap-1 rounded px-2 py-1 text-sm text-left
hover:bg-gray-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

When value is empty (placeholder state):
```
text-gray-400 italic
```

---

## 2. Pencil icon

```
h-3.5 w-3.5 shrink-0 text-gray-400 opacity-0 group-hover:opacity-100 group-focus-visible:opacity-100
```

The pencil icon appears only on hover or focus-visible of the group button.

---

## 3. Edit mode input recipe

```
block w-full rounded border border-blue-500 bg-white px-2 py-1 text-sm
focus:outline-none focus:ring-2 focus:ring-blue-500
```

The edit input has an explicit `border-blue-500` to signal "editing" state
independently of the host's surrounding styling.

---

## 4. Visual state inventory

| State | Mode | Recipe |
|---|---|---|
| idle | view | transparent bg, no border |
| hover | view | `hover:bg-gray-100` |
| focus-visible | view | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| pencil visible | view (hover/focus) | `group-hover:opacity-100 group-focus-visible:opacity-100` |
| empty | view | `text-gray-400 italic` |
| editing | edit | `border-blue-500 bg-white`, focus ring `ring-2 ring-blue-500` |
