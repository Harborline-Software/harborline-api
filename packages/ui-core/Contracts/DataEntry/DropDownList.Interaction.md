# DropDownList — Interaction Contract

- **Component:** DropDownList
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownList.Semantic.md) · [Accessibility](./DropDownList.Accessibility.md) · [Styling](./DropDownList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownList.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Selection

`onChange` on the native `<select>` fires when user selects an option. `e.target.value === ''` → `null`; otherwise the raw string from the option value. Calls `onValueChange(value | null)`.

---

## 2. Keyboard / mouse

All keyboard and mouse interaction delegated to the native `<select>` element. No custom handlers added.

---

## 3. Loading state

When `loading=true`, the select and toggle display show `'Loading...'` / `'⟳'` and the select is `disabled`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDL1 | Medium | `onValueChange` always fires a `string` (native select value) even when the original data item had a `number` value — type mismatch possible | Accepted-risk M1 |
| G-DDL2 | Low | Native `<select>` appearance varies across OS/browser — custom styling only applies to the wrapper div, not the dropdown itself | Accepted-risk M1 |
