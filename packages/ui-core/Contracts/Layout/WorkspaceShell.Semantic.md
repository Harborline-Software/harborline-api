# WorkspaceShell — Semantic Contract

- **Component:** WorkspaceShell
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Accessibility](./WorkspaceShell.Accessibility.md) · [Interaction](./WorkspaceShell.Interaction.md) · [Styling](./WorkspaceShell.Styling.md)
- **Reference implementation:** `packages/workspace-shell/src/WorkspaceShell.tsx`
- **Framework-neutral state contract:** `_shared/design/workspace-shell/workspace-shell.js`
- **Phase:** ADR 0017-A1 Phase M0

## Purpose

`WorkspaceShell` is the common application-workspace frame used by the component gallery and
translation review. It owns chrome behavior; consumers own panel content. A behavior difference
between consumers that is not expressed by a documented prop is a defect.

## Regions and slots

The root carries `data-workspace-shell` and a stable `data-shell-id`. It exposes four named regions:

| Region | Required landmark | Consumer slot |
|---|---|---|
| `navigation` | `nav` | start/left panel |
| `main` | `main` | primary task |
| `inspector` | `aside` | end/right panel |
| `utility` | `aside` | bottom panel/status |

The composite also exposes an ecosystem-bar embed point, ecosystem search slot, header title,
badge, switcher, and leading/trailing header slots. The switcher is consumer-owned content in the
shared shell position: React/Blazor for the gallery; List/Card for translation review.

## Public state

```typescript
type Viewport = 'wide' | 'standard' | 'compact' | 'narrow';
type NavigationMode = 'hidden' | 'rail' | 'docked' | 'overlay';
type InspectorMode = 'hidden' | 'docked' | 'overlay' | 'maximized';
type UtilityMode = 'hidden' | 'collapsed' | 'docked' | 'overlay' | 'maximized';

interface WorkspaceShellPreferences {
  navigation: NavigationMode;
  inspector: InspectorMode;
  utility: UtilityMode;
  pinnedSummary: boolean;
}
```

Preferences persist under `harborline-workspace-shell:v1:<shell-id>`. Responsive effective modes
are derived and MUST NOT overwrite the user's persisted wide-screen preferences. Corrupt or old
records fall back to declared defaults.

## Controls

The composite emits commands `toggle-navigation`, `toggle-inspector`, `toggle-utility`, and
`show-shortcuts`. Menu icon buttons and optional edge chevrons are two affordances for the same
commands and state. Both carry the same `aria-controls`, `aria-expanded`, disabled state, and
keyboard shortcut metadata.

## References

- ADR 0017 — spec-first UI contracts
- ADR 0022 — demo-page panel shape
- `_shared/design/workspace-shell/README.md`
