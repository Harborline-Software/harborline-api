# ToggleSwitch — Semantic Contract (Redirect)

- **Component:** ToggleSwitch
- **Status:** Deprecated — consolidated into Switch
- **Contract type:** Semantic

> **This component has been consolidated into Switch.**
> ToggleSwitch was a functional duplicate of Switch with a subset of features.
> See [Switch.Semantic.md](./Switch.Semantic.md) for the unified contract.
>
> ToggleSwitch's unique `description` prop has been folded into Switch
> as `description?: string` (renders subtitle text below the label).
>
> Migrate:
> - Replace `<ToggleSwitch label="..." />` with `<Switch label="..." />`
> - Replace `<ToggleSwitch description="..." />` with `<Switch description="..." />`
> - `onChange` on ToggleSwitch maps to `onCheckedChange` on Switch
>
> ToggleSwitch will be removed in the next major release.
