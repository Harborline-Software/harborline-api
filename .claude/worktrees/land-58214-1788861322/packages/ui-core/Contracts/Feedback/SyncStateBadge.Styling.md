# SyncStateBadge — Styling Contract

- **Component:** SyncStateBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SyncStateBadge.Semantic.md) · [Interaction](./SyncStateBadge.Interaction.md) · [Accessibility](./SyncStateBadge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/SyncStateBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`inline-flex items-center gap-1.5 text-xs text-muted-foreground` + `className` passthrough.

Uses `text-muted-foreground` design token.

---

## 2. Dot

`h-2 w-2 rounded-full` + state dot class, `aria-hidden="true"`.

| `state` | Dot class |
| --- | --- |
| `synced` | `bg-success` |
| `syncing` | `bg-primary animate-pulse` |
| `pending` | `bg-warning` |
| `error` | `bg-destructive` |
| `offline` | `bg-status-offline` |

---

## 3. Design token dependency

`bg-success`, `bg-primary`, `bg-warning`, `bg-destructive`, `bg-status-offline`,
and `text-muted-foreground` are design tokens. All must be present in the
consuming app's Tailwind theme configuration.
