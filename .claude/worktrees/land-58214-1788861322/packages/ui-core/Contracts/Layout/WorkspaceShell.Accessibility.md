# WorkspaceShell — Accessibility Contract

- **Component:** WorkspaceShell
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WorkspaceShell.Semantic.md) · [Interaction](./WorkspaceShell.Interaction.md) · [Styling](./WorkspaceShell.Styling.md)
- **Reference implementation:** `packages/workspace-shell/src/WorkspaceShell.tsx`

## Landmarks and names

- The four regions MUST use the landmarks in the semantic contract and have unique accessible
  names appropriate to their consumer.
- Every panel control is a native button with an accessible name, `aria-controls`, and live
  `aria-expanded`. Source and projected ecosystem-bar controls MUST agree after every transition.
- Splitters use `role="separator"`, `aria-orientation`, `aria-valuemin`, `aria-valuemax`,
  `aria-valuenow`, and `aria-controls`.

## Keyboard and focus

- `Mod+B` toggles navigation; `Mod+J` toggles utility; `Mod+Shift+/` opens shortcut help.
- Splitters support Arrow keys in 16px steps, Home/End to bounds, and Enter to toggle.
- The utility splitter is horizontal: Up increases bottom-panel height, Down decreases it, Home
  fully collapses it, End expands it to `45dvh`, and Enter follows the utility toggle contract.
- Utility exposes `aria-valuemin="0"`, a runtime pixel `aria-valuemax` equivalent to `45dvh`, and
  `aria-valuenow="0"` while collapsed. Its accessible name identifies the bottom panel it resizes.
- Escape closes transient overlays and returns focus to the invoking control when possible.
- Opening a transient panel moves focus into its landmark. Closing it MUST NOT leave focus on a
  hidden or removed element.
- The switcher follows its supplied native pattern (links with `aria-current`, or grouped buttons
  with `aria-pressed`). Cloned ecosystem-bar controls proxy the original native control.

## Responsive and touch

- Visible controls at touch layouts have at least 44×44 CSS-pixel targets.
- At narrow widths, secondary panel controls move into the preferences disclosure; the bar MUST
  not horizontally scroll.
- Overlays use a dismissible backdrop and remain keyboard operable.

## WCAG 2.2 AA floor

- Text contrast: 4.5:1; component boundaries, icons, and focus indicators: 3:1.
- Focus is visible and never removed.
- Panel state is never conveyed by color alone.
- State motion honors `prefers-reduced-motion: reduce`.

Relevant criteria: 1.4.3, 1.4.11, 2.1.1, 2.4.3, 2.4.7, 2.5.8, 4.1.2.
