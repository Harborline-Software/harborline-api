# SearchField — Styling Contract

- **Component:** SearchField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchField.Semantic.md) · [Interaction](./SearchField.Interaction.md) · [Accessibility](./SearchField.Accessibility.md) · [Styling](./SearchField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container recipe

```
relative flex items-center
```

---

## 2. Leading search icon

```
pointer-events-none absolute left-3 text-gray-400
```

SVG: `h-4 w-4`

---

## 3. Input recipe

```
block w-full rounded-md border py-2 pl-9 pr-9 text-sm text-gray-900
placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500
```

Default border: `border-gray-300 focus:border-blue-500`
Error border: `border-red-500 focus:ring-red-500`

---

## 4. Clear button recipe

```
absolute right-3 text-gray-400 hover:text-gray-600
```

No focus ring in M1 (known accessibility gap — see Accessibility G2).

---

## 5. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | border | `border-gray-300` |
| focus | border + ring | `focus:border-blue-500 focus:ring-2 focus:ring-blue-500` |
| error | border + ring | `border-red-500 focus:ring-red-500` |
| clear button visible | right slot | appears when `hasValue === true` |
