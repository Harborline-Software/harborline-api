# CheckBox — Interaction Contract

- **Component:** CheckBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CheckBox.Semantic.md) · [Accessibility](./CheckBox.Accessibility.md) · [Styling](./CheckBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CheckBox.tsx`
- **Catalog row:** #24 Checkbox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Click / keyboard toggle

Native `<input type="checkbox">` handles all click and Space/Enter toggling. No custom keyDown handler is added. Browser default behavior applies.

---

## 2. Indeterminate reset behavior

When `checked='indeterminate'` and the user clicks:
- Browser fires a native change event with `e.target.checked = true` (indeterminate → checked transition)
- `onChange(true)` is called
- The indeterminate visual state is not automatically restored — the parent must re-set `checked='indeterminate'` to bring it back

---

## 3. Disabled state

When `disabled=true`, clicks and keyboard interaction are blocked by the native input `disabled` attribute. No custom prevention logic needed.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CB4 | Low | No `onFocus`/`onBlur` props — consumers cannot react to focus events without wrapping | Accepted-risk M1 |
