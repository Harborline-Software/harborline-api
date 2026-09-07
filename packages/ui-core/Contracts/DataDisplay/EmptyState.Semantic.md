# EmptyState — Semantic Contract

- **Component:** EmptyState
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./EmptyState.Interaction.md) · [Styling](./EmptyState.Styling.md) · [Accessibility](./EmptyState.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** A18 EmptyState (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled placeholder layout `<div>` wrapper

---

## 1. Purpose

EmptyState is the canonical placeholder surface shown when a list / grid / panel
has no rows or content to display. It standardises the three messaging moods the
app uses — informational ("nothing here yet"), positive ("all clear"), and
actionable ("create the first one") — so empty regions across the app feel like
one design vocabulary rather than ad-hoc text blocks.

EmptyState pairs naturally with DataGrid (passed via the `emptyState` slot) and
with any other list-bearing component that lacks a data-driven primary view.

---

## 2. Data model

EmptyState has no internal data model. It is a presentational composition of an
icon + title + optional description + optional single CTA button.

```typescript
type EmptyStateVariant = 'informational' | 'positive' | 'actionable'

interface EmptyStateAction {
  label: string
  onClick: () => void
}

interface EmptyStateProps {
  variant: EmptyStateVariant
  title: string
  description?: string
  action?: EmptyStateAction
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'informational' \| 'positive' \| 'actionable'` | _required_ | Mood / intent of the empty state. Drives the leading icon and (per PAO Styling) the icon colour. |
| `title` | `string` | _required_ | The primary message ("No invoices yet", "All caught up", "Add your first property"). One line; sentence case. |
| `description` | `string` | — | Optional secondary line explaining the state or hinting at next steps. |
| `action` | `EmptyStateAction` | — | Optional single CTA — `{ label, onClick }`. When supplied, renders a button below the description. |

### 3.1 Variant semantics

| Variant | Meaning | Icon (M1 baseline) | Typical use |
| --- | --- | --- | --- |
| `informational` | Neutral "no data here". | `Info` (lucide) | Empty list whose population is data-driven and outside the user's immediate workflow (e.g. read-only report with no matching rows). |
| `positive` | Successful zero-state. | `CheckCircle2` (lucide) | "All tasks done", "Inbox zero", "No errors detected". Communicates that empty is the desired outcome. |
| `actionable` | The user is expected to act. | `PlusCircle` (lucide) | "Create your first invoice", "Add a property". Almost always paired with an `action` CTA. |

The icon is a hard-coded `lucide-react` import per variant in M1. Slotting a
custom icon is a deferred feature (§7).

### 3.2 Layout

The implementation renders a centred column: icon → title → optional
description → optional CTA, with vertical padding `py-12 px-6`. Detailed
spacing and token surface are owned by PAO Styling.

### 3.3 HTML attribute passthrough

The current implementation does **not** spread arbitrary HTML attributes on the
root `<div>`. Same known gap as DataGrid and Pager — a future contract
amendment will close it.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `action.onClick` | `()` | The user activates the CTA button (click or Enter/Space when focused). Only fires when `action` is supplied and the user activates the button. |

EmptyState has no other event surface — it is a presentational component.

---

## 5. Slots

EmptyState has **no slot extensibility** in M1. The icon, title, description,
and CTA are all driven by props with no `ReactNode` overrides. Slot-style
extensibility for any of these positions is a deferred feature (§7).

---

## 6. Component composition

- **DataGrid pairing.** EmptyState is the canonical content for DataGrid's
  `emptyState` slot. Hosts that want the standard look for "no rows" pass
  `<EmptyState variant="informational" title="No results" />` rather than the
  built-in `<p>No results.</p>` fallback.
- **Panel / card empty zones.** Any list-bearing panel or card can use
  EmptyState directly as its body content when its data array is empty.
- **Page-level empty states.** Larger "empty page" surfaces (e.g. an entire
  route showing no content yet) should still use EmptyState, possibly inside
  a wrapping section. Page-specific illustrations (custom artwork instead of
  the variant icon) are out of scope for M1.

---

## 7. Deferred features

Explicitly **out of scope** for the M1 baseline:

- **Custom icon slot** (`icon?: ReactNode`) — to override the variant default.
- **Custom illustration slot** — for page-level empty states that want bespoke
  artwork.
- **Multiple actions** — current model is exactly 0 or 1 `action` button.
  Hosts needing primary + secondary actions must wrap EmptyState or wait.
- **Variant for `error`** — a fourth variant for error/failure empty states
  (e.g. "Failed to load") may join the set in a later wave.
- **HTML attribute passthrough on root** — stable `id`, `data-testid`, ARIA
  linkage.

---

## 8. Open questions (for council)

1. **Variant naming.** Are `informational` / `positive` / `actionable` the
   right vocabulary? Alternative axes might be `info / success / cta` or
   `neutral / positive / call-to-action`. (Leaning: keep current naming —
   it's already shipping and the words communicate intent without locking
   to a specific design language.)
2. **Icon-slot extensibility.** §7 lists this as deferred. Should it move
   into M1 (since the shipping implementation already supports per-variant
   icons; adding a custom-icon override is trivial)?
3. **Action button variant.** When `variant === 'actionable'`, should the
   CTA button automatically take a primary/filled treatment (vs the current
   neutral outline)? Or should the button styling be variant-agnostic and
   driven entirely by PAO Styling tokens?

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/datagrid/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
