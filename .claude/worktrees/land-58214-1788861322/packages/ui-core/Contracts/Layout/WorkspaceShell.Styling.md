# WorkspaceShell — Styling Contract

- **Component:** WorkspaceShell
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WorkspaceShell.Semantic.md) · [Accessibility](./WorkspaceShell.Accessibility.md) · [Interaction](./WorkspaceShell.Interaction.md)
- **Reference stylesheet:** `_shared/design/workspace-shell/workspace-shell.css`

## Token surface

| Token | Purpose |
|---|---|
| `--shell-border` | region dividers and splitter grips |
| `--shell-surface` | main surface |
| `--shell-raised` | panel and overlay surface |
| `--shell-text` | panel foreground |
| `--shell-focus` | focus-visible outline |
| `--shell-navigation-size` | start panel width |
| `--shell-inspector-size` | end panel width |
| `--shell-utility-size` | bottom panel height |

Tokens resolve from `--eco-*` first, then Harborline semantic tokens, then system colors. Consumers
MUST NOT fork panel geometry or state selectors in app-private CSS.

## Geometry

Wide mode uses named grid areas for navigation, main, inspector, and utility. Hidden/transient
state changes grid tracks rather than overlaying blank app-private columns. Overlay modes use the
shared z-index scale and a shared backdrop. Resting panels are flat; borders and tonal layers create
structure without decorative shadows.

The shared `.stack-switch` treatment is the only workspace-header segmented style. Consumers may
place framework links or view-mode buttons in its segments, but MUST NOT re-declare its border,
radius, typography, focus, active, or disabled states.

## Responsive rules

- The generated ecosystem bar owns its 40px row and safe-area padding.
- At `<768px`, the search reduces to an icon and workspace controls move to preferences.
- Visible touch controls are 44px; no row may overflow at 360px.
- Panel overlays fit within the viewport and scroll internally.

## Motion

Only state transitions animate. Layout and chevron transitions use the shared reduced-motion rule;
there is no decorative entrance choreography.
