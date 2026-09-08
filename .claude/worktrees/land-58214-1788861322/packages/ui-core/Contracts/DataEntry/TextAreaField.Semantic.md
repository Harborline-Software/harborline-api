# TextAreaField — Semantic Contract (Redirect)

- **Component:** TextAreaField
- **Status:** Deprecated — consolidated into TextArea
- **Contract type:** Semantic

> **This component has been consolidated into TextArea.**
> TextAreaField is now a thin deprecated shim that delegates to TextArea.
> See [TextArea.Semantic.md](./TextArea.Semantic.md) for the unified contract.
>
> Migrate: replace `<TextAreaField>` with `<TextArea>` inside `<FormField>`.
> TextArea now reads FormFieldContext automatically.
> Note: the `invalid` prop on TextArea is deprecated — use `error` instead.
>
> TextAreaField will be removed in the next major release.
