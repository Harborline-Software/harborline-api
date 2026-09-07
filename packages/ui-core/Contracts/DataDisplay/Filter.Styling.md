# Filter — Styling Contract

- **Component:** Filter
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Filter.Semantic.md) · [Interaction](./Filter.Interaction.md) · [Accessibility](./Filter.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Filter.tsx`
- **Catalog row:** #59 Filter (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex flex-col gap-2`

---

## 2. Filter row

`flex items-center gap-2 flex-wrap`

---

## 3. Field and operator selects

`h-8 rounded-md border border-input bg-background px-2 text-sm`

---

## 4. Value input (text / number / date)

`h-8 rounded-md border border-input bg-background px-2 text-sm min-w-[120px]`
Placeholder: `Value…`

---

## 5. Value input (boolean)

`h-8 rounded-md border border-input bg-background px-2 text-sm`
(same as field/operator select; options: True / False)

---

## 6. Remove button

`h-8 w-8 flex items-center justify-center`
`text-muted-foreground hover:text-destructive rounded-md hover:bg-destructive/10`
Content: `×` character

---

## 7. Add filter button

`self-start text-sm text-primary hover:underline`
Content: `+ Add filter`
