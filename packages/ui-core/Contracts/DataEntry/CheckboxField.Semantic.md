# CheckboxField — Semantic Contract (Redirect)

- **Component:** CheckboxField
- **Status:** Deprecated — consolidated into CheckBox
- **Contract type:** Semantic

> **This component has been consolidated into CheckBox.**
> CheckboxField is now a thin deprecated shim that delegates to CheckBox.
> See [CheckBox.Semantic.md](./CheckBox.Semantic.md) for the unified contract.
>
> Migrate: replace `<CheckboxField>` with `<CheckBox>` inside `<FormField>`.
> CheckBox now reads FormFieldContext automatically.
> Note: CheckboxField's `onChange: (boolean)` maps to CheckBox's `onChange: (boolean | 'indeterminate')`;
> the 'indeterminate' value is never passed through the shim.
>
> CheckboxField will be removed in the next major release.
