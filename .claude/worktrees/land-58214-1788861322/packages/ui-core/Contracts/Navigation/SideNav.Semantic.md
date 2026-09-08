# SideNav — Semantic Contract (Redirect)

- **Component:** SideNav
- **Status:** Deprecated — consolidated into Menu
- **Contract type:** Semantic

> **This component has been consolidated into `<Menu variant="sidenav">`.**
> See [Menu.Semantic.md](./Menu.Semantic.md) for the unified contract.
>
> SideNav is now a thin deprecated passthrough shim that delegates to
> `<Menu variant="sidenav">`. All props map 1:1 (`items`, `activeItemId`,
> `collapsed`, `onItemActivate`, `className`).
>
> Migrate: replace `<SideNav>` with `<Menu variant="sidenav">` — no prop changes needed.
>
> SideNav will be removed in the next major release.
