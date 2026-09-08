# ImageUploader — Styling Contract

- **Component:** ImageUploader
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ImageUploader.Semantic.md) · [Interaction](./ImageUploader.Interaction.md) · [Accessibility](./ImageUploader.Accessibility.md) · [Styling](./ImageUploader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ImageUploader.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Drop zone base recipe

```
relative overflow-hidden rounded-xl border-2 transition-colors
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

In empty state:
```
border-dashed p-6 flex flex-col items-center justify-center gap-2
```

In preview state:
```
border-gray-200 p-0
```

---

## 2. Drop zone state overlays

| State | Condition | Recipe |
|---|---|---|
| **Idle (empty)** | enabled, no drag, no value | `border-gray-300 cursor-pointer hover:border-blue-400 hover:bg-blue-50/30` |
| **Dragging** | drag active | `border-blue-400 bg-blue-50` |
| **Disabled** | `disabled === true` | `cursor-not-allowed opacity-50 bg-gray-50` |

---

## 3. Preview state

Preview image: `w-full h-40 object-cover`

Remove button overlay:
```
absolute top-2 right-2 h-6 w-6 rounded-full bg-black/50 text-white
flex items-center justify-center text-xs
hover:bg-black/70 focus:outline-none focus-visible:ring-2 focus-visible:ring-white
```

---

## 4. Empty state

Upload icon container: `h-10 w-10 rounded-full bg-gray-100 flex items-center justify-center text-gray-400 text-xl`

Placeholder text: `text-sm text-gray-500 text-center`

Size hint: `text-xs text-gray-400`

---

## 5. Error message

```
text-xs text-red-600
```

---

## 6. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| empty idle | border | `border-dashed border-gray-300` |
| empty hover | border | `hover:border-blue-400` |
| empty dragging | border + bg | `border-blue-400 bg-blue-50` |
| preview | border + layout | `border-gray-200 p-0` |
| disabled | opacity + bg | `opacity-50 bg-gray-50 cursor-not-allowed` |
| focus-visible | ring | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| error text | text | `text-xs text-red-600` |
