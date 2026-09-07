# BadgeContainer — Semantic Contract

- **Component:** BadgeContainer
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **Companion contracts:** [Interaction](./BadgeContainer.Interaction.md) · [Styling](./BadgeContainer.Styling.md) · [Accessibility](./BadgeContainer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/BadgeContainer.tsx`

---

## 1. Purpose

`BadgeContainer` is a presentational layout wrapper that establishes the
positioning context for an overlay `Badge`. It renders a single `<div>` with
`position: relative` + `display: inline-flex` around its children so a `Badge`
composed as a sibling can be absolutely positioned to a corner of the wrapped
element — count bubbles on an icon button, status dots on an avatar — without
the host having to add the relative wrapper itself.

It owns no badge state and renders no badge of its own. Its sole responsibility
is the anchor box; the overlay `Badge` (and its `position` / `align`) is
composed by the host as a child. `NumberBadge` and `NotificationDot` build on
this wrapper internally (Badge.Semantic §16.3 / §16.5).

## 2. Data model

```typescript
export interface BadgeContainerProps extends React.HTMLAttributes<HTMLDivElement> {
  /** The element(s) the badge is anchored against — typically a single icon, avatar, or control. */
  children: React.ReactNode
  className?: string
}
```

All other `React.HTMLAttributes<HTMLDivElement>` (`id`, `data-*`, `aria-*`,
event handlers, `style`, …) are spread onto the underlying `<div>`.

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `children` | `ReactNode` (required) | — | The anchored content plus the overlay `Badge`. The first child is the visual element; the `Badge` positions itself against the wrapper. |
| `className` | `string` | — | Merged after the base `relative inline-flex` classes via `cn()`; lets the host size/space the wrapper. |
| `...rest` | `HTMLAttributes<HTMLDivElement>` | — | Forwarded verbatim to the wrapper `<div>` (id, data-*, aria-*, handlers). |

## 4. Events

None. `BadgeContainer` is purely presentational and adds no callbacks of its
own. Native handlers supplied via `...rest` forward to the underlying `<div>`.

## 5. Slots

A single `children` slot — the anchored content together with the overlay
`Badge`. There is no dedicated "badge" slot: the badge is an ordinary child and
positions itself relative to this wrapper. Composition order is the host's
(anchor element first, `Badge` after).

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/badges/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
