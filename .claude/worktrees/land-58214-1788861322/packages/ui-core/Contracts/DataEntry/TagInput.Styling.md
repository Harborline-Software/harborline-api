# TagInput — Styling Contract

- **Component:** TagInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TagInput.Semantic.md) · [Interaction](./TagInput.Interaction.md) · [Accessibility](./TagInput.Accessibility.md) · [Styling](./TagInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TagInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root layout

```
flex flex-col gap-1
```

---

## 2. Field container recipe

```
flex min-h-[2.25rem] flex-wrap items-center gap-1.5 rounded-md border bg-white px-2 py-1.5
focus-within:ring-2 focus-within:ring-blue-500 focus-within:border-blue-500
```

Error border: `border-red-400`
Default border: `border-gray-300`

Disabled overlay: `bg-gray-50 pointer-events-none opacity-60`

---

## 3. Tag chip recipe

```
inline-flex items-center gap-1 rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-700
```

---

## 4. Tag remove button recipe

```
text-blue-500 hover:text-blue-700 focus-visible:outline-none
```

No focus ring — known accessibility gap (see Accessibility G1).

---

## 5. Text input recipe

```
min-w-[6rem] flex-1 bg-transparent text-sm outline-none placeholder:text-gray-400
```

The input is unstyled (no border — the container provides the border) and
expands to fill available space via `flex-1`.

---

## 6. Label recipe

```
text-sm font-medium text-gray-700
```

---

## 7. Hint recipe

```
text-xs text-gray-500
```

---

## 8. Error recipe

```
text-xs text-red-600
```

---

## 9. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | container border | `border-gray-300` |
| focus-within | container border + ring | `focus-within:ring-2 focus-within:ring-blue-500 focus-within:border-blue-500` |
| error | container border | `border-red-400` |
| disabled | container | `bg-gray-50 pointer-events-none opacity-60` |
| tag chip | inline | `bg-blue-100 text-blue-700 rounded-full` |
