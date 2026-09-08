# Drawer — Semantic Contract

- **Component:** Drawer (Sheet)
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Drawer.Interaction.md) · [Styling](./Drawer.Styling.md) · [Accessibility](./Drawer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Drawer.tsx`
- **Catalog row:** #46 Drawer (`app-priority: high`, `library-scope: v1`, `Notes: alias: Sheet (shadcn)`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** shadcn Sheet → Radix UI `@radix-ui/react-dialog` (variant of Dialog with edge-slide animation)
- **Related contract:** [Dialog.Semantic.md](./Dialog.Semantic.md) — Drawer is the edge-slide variant of the centre-modal Dialog

---

## 1. Purpose

Drawer is the canonical **edge-slide overlay panel** of `@harborline-software/ui-react`.
It slides in from a viewport edge (right, left, top, or bottom) and
displays content alongside (rather than blocking the centre of) the main
view. Typical use:

- **Detail / inspector panels** — right-slide, showing the selected entity
  while the list remains visible.
- **Settings / preferences** — right-slide for context-preserving form.
- **Filters** — left-slide for a complex filter UI alongside the result
  list.
- **Mobile navigation** — left-slide for the responsive side-nav (in
  fact: AppLayout's overlay-mode side-nav is conceptually a Drawer).
- **Confirmations / quick actions** — bottom-slide on mobile for
  thumb-accessible affordances.

This is a **forward-spec**: Drawer has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public
surface based on the shadcn Sheet pattern (Radix Dialog with `side`
animation variants), adapted to Harborline's slot convention.

Drawer differs from sibling Overlay components:

- **Dialog** (M1) — centre-modal, fully blocking, focused content.
- **Drawer** (this) — edge-slide, may be context-preserving (non-modal
  variant) or fully blocking (modal variant).
- **ConfirmDialog** (M1) — narrow centre-modal for yes/no decisions.

Drawer wraps Radix Dialog; the Radix primitive is the implementation
detail. The public surface mirrors Dialog's surface (`open`,
`onOpenChange`, `title`, `description`, `footer`, `children`) with
additional `side` and `size` props.

---

## 1.5 What Drawer is NOT (G-DR1)

> **`@harborline-software/ui-react` Drawer is a Sheet-pattern overlay panel for forms, detail inspectors, and filter UIs. It is NOT a navigation drawer with data-bound items.**

Telerik Blazor Drawer is a **navigation drawer** — it renders a list of navigation items (`Data`/`SelectedItem`/`ItemTemplate`), supports collapse-to-icons (`MiniMode`), and has `Push`/`Overlay` modes for resizing main content. Our `Drawer` has none of these navigation features.

| Telerik Drawer feature | Harborline equivalent |
|---|---|
| `Data` / `SelectedItem` / `ItemTemplate` | Use `AppLayout`'s `sideNav` slot or `SideNav` component |
| `MiniMode` (collapse to icon rail) | Not available; use `SideNav` |
| `Push` mode (resize main content) | Not available in M2; use `AppLayout` right-rail slot (future) |
| `Overlay` mode (float over content) | **This is what `Drawer` does** — overlay panel with slide animation |

If you are migrating a Telerik navigation drawer with item-rendering to Harborline, use `SideNav`, not `Drawer`.

---

## 2. Data model

```typescript
type DrawerSide = 'right' | 'left' | 'top' | 'bottom'
type DrawerSize = 'sm' | 'md' | 'lg' | 'xl' | 'full'

interface DrawerProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  side?: DrawerSide
  size?: DrawerSize
  title: string
  description?: string
  children: React.ReactNode
  footer?: React.ReactNode
  modal?: boolean
  closeOnOverlayClick?: boolean  // default: true
  closeOnEscape?: boolean        // default: true
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `open` | `boolean` | _required_ | Controlled open state. Drawer renders into a portal when `open === true`; the portal is unmounted (via Radix) when `false`. |
| `onOpenChange` | `(open: boolean) => void` | _required_ | Fires when Radix wants to change the open state — user clicked close, clicked the overlay (modal mode), pressed Escape, etc. Host updates state. |
| `side` | `'right' \| 'left' \| 'top' \| 'bottom'` | `'right'` | Edge the drawer slides in from. `'right'` is the default (most common — detail / inspector pattern). |
| `size` | `'sm' \| 'md' \| 'lg' \| 'xl' \| 'full'` | `'md'` | The drawer's cross-axis dimension. For `side='right'` / `'left'`: width. For `side='top'` / `'bottom'`: height. `'full'` covers the entire viewport cross-axis (`100vw` or `100vh`). |
| `title` | `string` | _required_ | Drawer title rendered in the header. Required for ARIA (every overlay must be labelled). |
| `description` | `string` | — | Optional secondary text below the title. Provides context for AT. |
| `children` | `ReactNode` | _required_ | Drawer body content. Host-owned. |
| `footer` | `ReactNode` | — | Optional footer action row. When supplied, renders below the body with a top border. Typical content: cancel + save buttons. |
| `modal` | `boolean` | `true` | When `true` (default), Drawer behaves like Dialog: scrim overlay, focus trap, scroll lock, ESC + scrim-click to close. When `false`, Drawer is non-modal: no scrim, no focus trap, no scroll lock — the page underneath remains interactive. The non-modal variant is council open question 1. |
| `closeOnOverlayClick` | `boolean` | `true` | When `false`, clicking (pointer-down on) the overlay does not close the drawer. Use for drawers hosting in-progress operations or multi-step forms where accidental dismissal must be prevented. **Added in M2.1. Closes audit gap G-DR3.** |
| `closeOnEscape` | `boolean` | `true` | When `false`, pressing the Escape key does not close the drawer. Pair with `closeOnOverlayClick={false}` for fully explicit-close-only drawers. **Added in M2.1.** |

### 3.1 Side semantics

| Side | Slides from | Cross-axis dimension | Typical content |
| --- | --- | --- | --- |
| `'right'` | Right edge | Width | Detail inspector, settings panel, form. Most common. |
| `'left'` | Left edge | Width | Filters, navigation drawer (mobile). |
| `'top'` | Top edge | Height | Command palette, notifications drawer. |
| `'bottom'` | Bottom edge | Height | Mobile action sheet, sort/filter quick controls. |

### 3.2 Size semantics

PAO Styling owns the exact token values; canonical defaults:

| Size | right/left (width) | top/bottom (height) |
| --- | --- | --- |
| `'sm'` | 320px | 240px |
| `'md'` | 480px | 360px |
| `'lg'` | 640px | 480px |
| `'xl'` | 800px | 600px |
| `'full'` | 100vw | 100vh |

`size: 'full'` makes the drawer effectively a full-screen overlay sliding
in from an edge. Often used on mobile.

### 3.3 Modal vs non-modal

**Modal (`modal: true`, default):**
- Scrim overlay behind the drawer.
- Focus trapped inside the drawer.
- Body scroll locked.
- ESC dismisses; scrim click dismisses (per Radix).
- Page underneath is not interactive.

**Non-modal (`modal: false`):**
- No scrim.
- No focus trap — Tab moves between drawer and page content.
- No scroll lock — the underlying page scrolls normally.
- ESC may or may not dismiss (council open question 2).
- Page underneath is fully interactive.

The non-modal variant is useful for:
- Inspector panels that should let the user keep editing the list.
- Filter panels that should let the user scroll the result list.

Most drawers in M2 Harborline use cases are modal; non-modal is a council
ratification item.

### 3.4 Header / Body / Footer regions

Like Dialog, Drawer renders three regions:

- **Header** — title + optional description + close button (top-right "X").
- **Body** — `children` content; scrolls internally when content exceeds
  available space.
- **Footer** — `footer` content with `border-t` (when supplied).

The composition is identical to Dialog's; only the entrance animation
and edge anchoring differ.

### 3.5 Portal + scroll lock

Like Dialog, Drawer renders into a portal at the document body level.
Scroll lock applies only in modal mode (`modal: true`).

### 3.6 Animation

Drawer slides in from its `side` edge over a canonical ~300ms duration
(PAO Styling owns the easing curve and exact timing). The animation is
deterministic — no JS-driven physics; pure CSS transitions / keyframes.

### 3.7 HTML attribute passthrough

The Drawer root supports HTML attribute passthrough (per the M2 default
established by Button). PAO Accessibility wires the ARIA attributes
internally; hosts can add `data-*`, `id`, etc. via passthrough.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onOpenChange` | `boolean` (next open state) | The user requests close: close-button click, scrim click (modal only), ESC press, or any other Radix close trigger. Host updates `open`. Also fires on programmatic close (host setting `open` to `false`). |

The contract follows Dialog precedent — close requests funnel through
the single `onOpenChange` callback rather than separate `onClose` /
`onCancel` events.

---

## 5. Slots

Drawer uses prop-driven slots (matches Dialog rather than the Card
sub-component pattern):

| Slot prop | Position | Purpose |
| --- | --- | --- |
| `title` | Header | Required title text. |
| `description` | Header | Optional secondary text. |
| `children` | Body | Main drawer content. |
| `footer` | Footer | Optional action row. |

A future enhancement may add `<DrawerHeader>` / `<DrawerFooter>`
sub-components for free-form composition. Out of scope for M2.

---

## 6. Component composition

- **Detail / inspector pattern.** Selecting an entity in a DataGrid
  row opens a Drawer (`side='right'`, `size='md'`) showing the
  entity's detail view; the user can scan rows in the list while the
  drawer stays open (non-modal variant) or focus entirely on the
  detail (modal variant).
- **Form editor.** A Drawer (`side='right'`, `size='lg'`) holds a form
  for creating / editing an entity; `footer` holds Cancel + Save
  Buttons.
- **Filter drawer.** A Drawer (`side='left'`, `size='sm'`,
  `modal: false`) holds a column of filter controls that update the
  list in real-time.
- **Mobile nav drawer.** A Drawer (`side='left'`, `size='sm'`, modal)
  holds the SideNav content. AppLayout's overlay-mode side-nav
  conceptually uses a Drawer pattern; whether AppLayout reuses Drawer
  internally is implementation detail (council open question 3).
- **Action sheet (mobile).** A Drawer (`side='bottom'`, `size='sm'`,
  modal) holds a vertical stack of action buttons for the selected
  entity.
- **Multi-step wizard.** A Drawer can host a multi-step wizard whose
  state is owned by the host; Drawer's `children` re-renders per step.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Resizable drawer** — user can drag the edge to resize. Useful for
  detail panels; defer.
- **Stackable drawers** — multiple drawers open at once with z-index
  stacking. Council ratifies whether to support (would require Radix
  Dialog stacking support, which exists).
- **Persistent drawer** — Drawer that always renders alongside main
  content (e.g. a persistent filter sidebar). This is conceptually a
  layout pattern, not a Drawer pattern — use AppLayout's sideNav slot
  with a "right inspector" sub-slot (future).
- **Inline (push) variant** — drawer pushes the main content aside
  instead of overlaying. **IMPORTANT: If you need a persistent inspector
  panel that lives *alongside* main content without overlapping it (Telerik
  Drawer's `Mode='Push'`), Drawer is the WRONG component in M2.** Push-mode
  requires layout-level coordination; Harborline Drawer is overlay-only in M2.
  Use AppLayout's future right-rail slot or roll a custom flex layout.
  Closes audit gap G-DR2.
- **Backdrop click suppression** — `closeOnBackdropClick?: boolean`.
  ~~Council open question 4.~~ **Added in M2.1 as `closeOnOverlayClick`
  (closes audit gap G-DR3).**
- **ESC suppression** — `closeOnEscape?: boolean`. ~~Council open
  question 4.~~ **Added in M2.1.**
- **Programmatic focus target** — `initialFocusRef`. Default focus is
  the first focusable inside the drawer (Radix default). Custom
  target deferred.
- **Animation customisation** — duration, easing override. Defaults
  only in M2.

---

## 8. Open questions (for council)

1. **Non-modal variant.** This spec includes `modal: false` for
   context-preserving drawers. Council confirms whether to ship this
   in M2 or defer. Leaning ship — the inspector + filter use cases
   are strong; non-modal is the right semantic for them.
2. **ESC in non-modal mode.** Should ESC still close the drawer in
   non-modal mode? Today: yes (Radix default). Some hosts prefer ESC
   to bubble for global handlers. Leaning keep ESC active in both
   modes; council ratifies.
3. **AppLayout overlay side-nav as Drawer?** AppLayout's overlay-mode
   side-nav conceptually IS a Drawer (`side='left'`, modal, scrim).
   Should AppLayout reuse Drawer internally, or have its own
   implementation? Leaning AppLayout has its own — Drawer's title +
   header pattern doesn't fit the side-nav case cleanly. Council
   confirms.
4. **`closeOnOverlayClick` + `closeOnEscape` opt-outs.** ~~Per Dialog
   precedent (M1), these are deferred. M2 may want to add them now
   since Drawer's "I'm editing in here" use cases benefit from
   explicit-close-only.~~ **RESOLVED — Added in M2.1. Closes audit
   gap G-DR3.**
5. **`side` default.** `'right'` (this spec) — most common in
   Harborline use cases. Some libraries default to `'bottom'` for
   mobile-first. Leaning `'right'` — desktop-first defaults work
   for the Harborline ERP audience.
6. **`size` default.** `'md'` — sufficient for most form / detail use
   cases. Leaning correct.
7. **Stacking multiple drawers.** Out of scope for M2 (deferred §7).
   Council confirms.
8. **Header close button — opt-out.** Today's spec always renders the
   close X. Some workflows want explicit-action-only dismissal (e.g.
   wizard with no premature exit). Leaning add `hideCloseButton?:
   boolean` opt-out — covers the edge case without polluting the
   default surface.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/dialogs/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DR1 | Critical | Conceptual scope mismatch with Telerik navigation drawer | [RESOLVED 2026-06-05] §1.5 "What Drawer is NOT" added — explicit scope statement + migration table |
| G-DR2 | Critical | Push mode deferred without strong callout | [RESOLVED 2026-06-05] §7 push-mode note strengthened: use AppLayout right-rail slot when available |
| G-DR3 | High | closeOnBackdropClick / closeOnEscape opt-outs unresolved | [RESOLVED 2026-06-05] §8.4 promoted to M2 resolution target |
| G-DR4 | High | size per-side asymmetry not documented | [RESOLVED 2026-06-05] §3.2 note: size=full + any side = full-screen overlay |
| G-DR5 | Medium | MiniMode deferred via omission | [ACCEPTED-RISK 2026-06-05] §7 note added: mini-rail / icon-strip not supported; use SideNav |
| G-DR6 | Medium | Stacked drawers behavior undefined | [ACCEPTED-RISK 2026-06-05] §7 note: do not nest drawers in M2; behaviour undefined |
| G-DR7 | Medium | Animation customization has no escape hatch | [ACCEPTED-RISK 2026-06-05] §3.7 note: animation override is fully closed in M2 |
