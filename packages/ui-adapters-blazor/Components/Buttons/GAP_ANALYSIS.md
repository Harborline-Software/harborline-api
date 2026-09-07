# Blazor buttons parity audit

> Verified 2026-07-16 against `packages/ui-react/src/components/buttons/` and the accepted
> component contracts in `packages/ui-core/Contracts/`.

This audit treats accepted UI Core contracts as the cross-framework source of truth. React-only
composition details such as Radix `asChild` are not copied into Blazor; Blazor-native equivalents
such as `RenderFragment`, `EventCallback`, and `AdditionalAttributes` preserve the same semantics.

## Contract-backed components

| React component | Blazor component | Status | Verification |
| --- | --- | --- | --- |
| Button | `HarborlineButton` | Conforming | Secondary default, native type, loading state, icon slots, attribute passthrough |
| ButtonGroup | `HarborlineButtonGroup` | Conforming | Group state, width, enabled state, selection modes, child button and toggle APIs |
| DropDownButton | `HarborlineDropDownButton` | Conforming | Typed items, appearance axes, disabled state, popup alignment, menu ARIA, close behavior |
| FloatingActionButton | `HarborlineFab` | Conforming | Compact/extended modes, theme and size, disabled state, accessible name, logical alignment |
| SplitButton | `HarborlineSplitButton` | Conforming | Primary action, typed alternatives, custom menu content, loading state, menu ARIA |

The accepted M1 contracts explicitly defer roving menu focus and arrow/Home/End navigation for
DropDownButton and SplitButton. Blazor matches that accepted risk while restoring focus to the menu
trigger after Escape or item activation where the framework can do so safely.

## React category components without UI Core contracts

| React component | Blazor status | Disposition |
| --- | --- | --- |
| ActionMenu | No one-to-one adapter | Use `HarborlineMenu` or `HarborlineContextMenu`; define a contract before adding another public API |
| CopyButton | No one-to-one adapter | React convenience composition; requires a contract before parity work |
| DataExportButton | No one-to-one adapter | React convenience composition; export behavior already exists in data components |
| IconButton | `HarborlineIconButton` | Adapter-native API; explicit `AriaLabel` closes the icon-only accessible-name gap |
| SegmentedControl | `HarborlineSegmentedControl` | Adapter-native API; not governed by a UI Core component contract |

## Gallery and gates

- Live overview demos cover Button loading/icon slots, DropDownButton actions, FAB compact and
  extended modes, and SplitButton typed items.
- Blazor contract tests cover defaults, loading states, menu semantics, disabled items, accessible
  names, and logical alignment.
- The button accessibility matrix includes the open DropDownButton menu at the WCAG 2.2 AA floor.

Future category work must start by accepting a UI Core contract for any React-only convenience
component. That keeps parity grounded in shared behavior instead of copying framework-specific APIs.
