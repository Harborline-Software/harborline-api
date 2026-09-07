# RadioGroup — Accessibility Contract

- **Component:** RadioGroup
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadioGroup.Semantic.md) · [Interaction](./RadioGroup.Interaction.md) · [Styling](./RadioGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RadioGroup.tsx`
- **Catalog row:** #105 RadioGroup (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="radiogroup"` | Container `<div>` | WAI-ARIA radiogroup widget |
| `aria-describedby` | Container `<div>` | From `useFormField()` context |
| `aria-invalid` | Container `<div>` | `true` when `error=true` |
| `type="radio"` | Each `<input>` | Native radio; implicit `role="radio"` |
| `name={name}` | Each `<input>` | Groups radios for browser native behavior |
| `checked={isChecked}` | Each `<input>` | Current selection |
| `disabled` | Each `<input>` | HTML disabled |

---

## 2. Label association

Each `<input>` is wrapped in a `<label>` element — native implicit association. No explicit `htmlFor`/`id` pair needed.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RG1 | Low | No `aria-label` on the `<div role="radiogroup">` container — AT may announce it as unnamed radiogroup | Accepted-risk M1; callers add via group-level label from `<FormField>` |
