# NavigationMenu — Semantic Contract (Redirect)

- **Component:** NavigationMenu
- **Status:** Deprecated — consolidated into Menu
- **Contract type:** Semantic

> **This component has been consolidated into `<Menu variant="navigation-menu">`.**
> See [Menu.Semantic.md](./Menu.Semantic.md) for the unified contract.
>
> The composable sub-exports (`NavigationMenuList`, `NavigationMenuItem`,
> `NavigationMenuTrigger`, `NavigationMenuContent`, `NavigationMenuViewport`,
> `NavigationMenuIndicator`, `NavigationMenuLink`) have been removed.
> Use the data-driven `MenuItemWithChildren[]` API on `<Menu variant="navigation-menu">`.
>
> Migrate: replace `<NavigationMenu>` and its composable children with
> `<Menu variant="navigation-menu" items={[...]} />`.
> Rich panel content is expressed via `item.panelContent: React.ReactNode`.
>
> NavigationMenu will be removed in the next major release.
