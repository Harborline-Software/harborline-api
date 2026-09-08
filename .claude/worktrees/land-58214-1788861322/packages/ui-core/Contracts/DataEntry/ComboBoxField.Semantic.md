# ComboBoxField — Semantic Contract (Redirect)

- **Component:** ComboBoxField
- **Status:** Deprecated — consolidated into ComboBox
- **Contract type:** Semantic

> **This component has been consolidated into ComboBox.**
> ComboBoxField is now a thin deprecated shim that delegates to ComboBox.
> See [ComboBox.Semantic.md](./ComboBox.Semantic.md) for the unified contract.
>
> Migrate: replace `<ComboBoxField options={opts}>` with `<ComboBox options={opts}>` inside `<FormField>`.
> ComboBox now accepts both `options: { value, label }[]` (from ComboBoxField) and
> `data: ComboBoxItem[]` (from the original ComboBox). ComboBox now reads FormFieldContext automatically.
>
> ComboBoxField will be removed in the next major release.
