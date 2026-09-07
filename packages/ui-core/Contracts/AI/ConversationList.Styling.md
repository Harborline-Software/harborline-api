# ConversationList — Styling Contract

- **Component:** ConversationList
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConversationList.Semantic.md) · [Interaction](./ConversationList.Interaction.md) · [Accessibility](./ConversationList.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/ConversationList.tsx`

---

## 1. Tokens

Token-driven throughout, no hardcoded colors: `border-border`, `bg-background`,
`bg-primary/5` (active row wash), `text-foreground`, `text-muted-foreground`,
`bg-destructive`/`text-destructive-foreground` (delete confirm), `focus-visible:ring-ring`.
Matches `NotificationCenter`'s row visual language (same active-row wash, same dismiss
button treatment) for family consistency across the AI/Navigation catalog.

## 2. Layout

`flex h-full flex-col` root — designed to fill a parent panel (e.g. a `Splitter` pane or a
`Window` body). Header (heading + "New conversation") is a fixed-height flex row; the row
list is `min-h-0 flex-1 overflow-y-auto` so it scrolls independently within a constrained
parent height.

## 3. RTL / logical properties

All spacing/alignment uses logical properties (`ps-*`/`pe-*`, `text-start`, `inset-e-*`)
— no `left`/`right`/`ml-`/`pl-` physical properties. The row action cluster uses `pe-2`
so it mirrors to the visual start under RTL automatically.

## 4. Reduced motion

No animation in v1 beyond the existing Tailwind `transition-colors` (hover/focus color
changes only — not a motion pattern gated by `prefers-reduced-motion`, consistent with
platform convention for color transitions vs. transform/opacity motion).

## 5. Density

Row padding (`px-3 py-2.5`) matches `NotificationCenter`'s `InfoRow` for visual rhythm
when both surfaces appear in the same app (the bell's notification list and Pilot's
conversation list are both "recent activity" lists).
