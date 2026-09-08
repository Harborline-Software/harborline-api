# Menubar — Semantic Contract (Redirect)

- **Component:** Menubar
- **Status:** Deprecated — consolidated into Menu
- **Contract type:** Semantic

> **This component has been consolidated into `<Menu variant="menubar">`.**
> See [Menu.Semantic.md](./Menu.Semantic.md) for the unified contract.
>
> The composable sub-exports (`MenubarMenu`, `MenubarTrigger`, `MenubarContent`,
> `MenubarItem`, `MenubarCheckboxItem`, `MenubarRadioGroup`, `MenubarRadioItem`,
> `MenubarSub`, `MenubarSubTrigger`, `MenubarSubContent`, `MenubarSeparator`)
> have been removed. Use the data-driven `MenubarItem[]` API on `<Menu variant="menubar">`.
>
> Migrate: replace `<Menubar>` and its composable children with
> `<Menu variant="menubar" items={[...]} />`.
>
> Menubar will be removed in the next major release.
