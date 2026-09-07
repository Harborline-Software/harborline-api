# WorkspaceShell — Interaction Contract

- **Component:** WorkspaceShell
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WorkspaceShell.Semantic.md) · [Accessibility](./WorkspaceShell.Accessibility.md) · [Styling](./WorkspaceShell.Styling.md)
- **Reference implementation:** `_shared/design/workspace-shell/workspace-shell.js`

## Viewport resolution

| Width | Viewport | Effective layout |
|---|---|---|
| `>=1360` | wide | persisted panel modes |
| `1024–1359` | standard | navigation docked/rail; side and bottom panels transient |
| `768–1023` | compact | all panels transient |
| `<768` | narrow | all panels transient; controls overflow into preferences |

Crossing a breakpoint closes transient panels but preserves wide-screen preferences.

## State transitions

- A wide/standard persistent toggle stores the current open mode in `lastOpen`, selects `hidden`,
  and restores `lastOpen` on the next toggle.
- A compact/narrow toggle changes transient state only.
- Escape closes all transient state and transient maximization without changing preferences.
- `restore-layout` resets declared defaults, panel sizes, and transient state.
- Disabled capabilities make their command, proxy, and shortcut no-ops.

### Narrow navigation drawer

Below 768px, transient navigation presents as a drawer over the workspace rather than sharing the
content layout. The drawer and its backdrop sit above consumer-owned sticky headers, filter bars,
and sorting controls on the shell's declared overlay layer, so every navigation row remains visible
and interactable while the drawer is open. Consumer stacking contexts MUST NOT overtake that layer.

## Resizing

Navigation is clamped to 180–520px and inspector to 220–720px. Utility is a variable bottom
splitter from fully collapsed (`0`) through `45dvh`; both pointer and keyboard resizing update the
same persisted size.

- Utility resize values at or below the 40px collapse threshold snap to `0`, select the persisted
  `collapsed` mode, close any transient utility overlay, and keep the splitter rail available.
- Resizing a collapsed utility above the threshold expands it and updates the same source and
  projected toggle state.
- Toggling or pressing Enter while utility is collapsed restores a content-sized default, not the
  last dragged size. The shell measures the utility's actual header and rows, clamps the result to a
  minimum of the header plus one complete content row and a maximum of `40dvh`, and never reserves
  unused vertical space. Consumers may declare a fallback size for content that cannot be measured;
  values below the minimum resolve to the shell default rather than reopening as a sliver. Toggling
  while expanded collapses it.
- Home collapses utility; End expands it to the current `45dvh` maximum. Arrow resizing is 16px per
  press; Up increases bottom-panel height and Down decreases it.

### Narrow bottom sheet

Below 768px, an opened utility is a full-width bottom sheet. Its open default is content-sized as
defined above, with `40dvh` as the default-height ceiling rather than a target. User drag may still
override that default through the same collapsed-to-`45dvh` range, and the sheet retains the desktop
snap, splitter, and keyboard grammar. Content starts at the sheet's block-start (`flex-start`); it is
never vertically centered.

The bottom sheet and the virtual keyboard are mutually exclusive. When a narrow viewport reports a
keyboard-reduced visual viewport while an editable control has focus, the utility sheet yields by
closing its transient presentation without changing the persisted wide-screen preference. The user
may reopen it after the visual viewport returns to its unoccluded height.

## Compact search overlay

Below 768px, the ecosystem-bar search occupies only its collapsed control box. The control and all
of its descendants MUST remain within that box; no intrinsic input width may enlarge or clip the
toolbar track.

Activating the collapsed control opens a portal/fixed search panel anchored immediately below the
ecosystem toolbar. The panel is outside document flow, spans the safe viewport width, and never
renders after the workspace content. Opening moves focus into the search input. Escape collapses the
panel and restores focus to the collapsed control. Keyboard search commands open the same panel and
follow the same focus lifecycle.

## Ecosystem-bar composition

The runtime observes the source header and projects its title, switcher, and panel controls into
the generated ecosystem-bar fragment. On narrow viewports, inspector/utility controls are removed
from the row and reproduced in the preferences disclosure. A projected button proxies its original
control, so React state and shell state remain authoritative.

## Reduced motion

Panel transitions are state feedback only, 160–220ms ease-out, and become effectively instant
under `prefers-reduced-motion: reduce`.
