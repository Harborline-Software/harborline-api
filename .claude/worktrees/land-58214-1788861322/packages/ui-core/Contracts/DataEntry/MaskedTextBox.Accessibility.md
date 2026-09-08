# MaskedTextBox — Accessibility Contract

- **Component:** MaskedTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedTextBox.Semantic.md) · [Interaction](./MaskedTextBox.Interaction.md) · [Styling](./MaskedTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MaskedTextBox.tsx`
- **Catalog row:** #82 MaskedTextBox (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="text"` | `<input>` | Standard text input |
| `id={id}` | `<input>` | For `<label htmlFor>` association |
| `name={name}` | `<input>` | For form submission |
| `placeholder` | `<input>` | Auto-generated mask placeholder (e.g. `"(___) ___-____"`) |
| `disabled` | `<input>` | When disabled |
| `readOnly` | `<input>` | When readonly |

---

## 2. Label association

`id` prop is exposed — host associates a visible label:
```tsx
<label htmlFor="phone">Phone number</label>
<MaskedTextBox id="phone" mask="(000) 000-0000" />
```

---

## 3. Placeholder as format hint

The auto-generated placeholder (e.g. `"(___) ___-____"`) tells users the expected format. For additional context, host should provide a visible label explaining the format.

---

## 4. Known gaps

None.
