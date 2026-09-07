# Callout — Semantic Contract

- **Component:** Callout
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Callout.Interaction.md) · [Accessibility](./Callout.Accessibility.md) · [Styling](./Callout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Callout.tsx`
- **Catalog row:** #A34 Callout (`app-priority: low`, `library-scope: planned`) — Harborline-native inline callout/note block
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` callout wrapper
- **Interaction-class:** display-only (no user interaction; render-only component)

---

## 1. Component purpose

**Callout** — a visually distinctive inline annotation block used in documentation and content pages. Similar to Alert but intended for editorial/docs contexts rather than user-action feedback.

---

## 2. Props

```typescript
type CalloutVariant = 'info' | 'tip' | 'warning' | 'error'

interface CalloutProps extends React.HTMLAttributes<HTMLDivElement> {
  variant?: CalloutVariant   // default: 'info'
  title?: string
  icon?: React.ReactNode     // default: per-variant SVG icon
}
```

Children render as the callout body.

---

## 3. Distinction from Alert

| Feature | Callout | Alert |
|---|---|---|
| Use context | Docs/editorial | User-action feedback |
| Dismiss button | No | Yes (optional) |
| Role | `role="alert"` on error only | `role="alert"`/`"status"` per variant |
| Action slot | No | Yes |
