# Dialog — Semantic Contract

- **Component:** Dialog
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Dialog.Interaction.md) · [Styling](./Dialog.Styling.md) · [Accessibility](./Dialog.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/dialogs/Dialog.tsx`
- **Catalog row:** #43 Dialog (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-dialog`

---

## 1. Purpose

Dialog is the canonical modal-overlay surface of `@harborline-software/ui-react`. It is a
controlled component built on Radix UI's `@radix-ui/react-dialog` primitive —
chosen for its accessible focus-trapping, ARIA `role="dialog"` + `aria-modal`
compliance, scroll-lock behaviour, and portalled rendering (escapes
`overflow:hidden` ancestors).

Dialog renders three regions:

- **Header** — title + optional description + close button.
- **Body** — host-supplied `children`.
- **Footer** — optional, host-supplied action row.

The contract treats the Radix primitive as an implementation detail; the
public surface is the props and the regional composition documented here.

---

## 2. Data model

Dialog is fully controlled. The host owns `open` and toggles it via
`onOpenChange(boolean)`.

```typescript
interface DialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description?: string
  children: React.ReactNode
  footer?: React.ReactNode
  closeOnOverlayClick?: boolean  // default: true
  closeOnEscape?: boolean        // default: true
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `open` | `boolean` | _required_ | Controlled open state. Dialog renders into a portal when `open === true`; the portal is unmounted (via Radix) when `false`. |
| `onOpenChange` | `(open: boolean) => void` | _required_ | Fires when Radix wants to change the open state — user clicked the close button, clicked the overlay, pressed Escape, etc. Host updates its own state. |
| `title` | `string` | _required_ | The dialog title, rendered in the header. Drives `<Dialog.Title>` — used by AT to announce the dialog on open. Required because ARIA mandates a labelled dialog. |
| `description` | `string` | — | Optional secondary text below the title in the header. Drives `<Dialog.Description>` — provides context to AT via `aria-describedby` on the dialog. |
| `children` | `ReactNode` | _required_ | Dialog body content. Host-owned. Goes in the central region between header and footer. |
| `footer` | `ReactNode` | — | Optional footer action row. When supplied, renders below the body with a top border. Typical content: cancel + confirm buttons. |
| `closeOnOverlayClick` | `boolean` | `true` | When `false`, clicking (pointer-down on) the overlay does not close the dialog. Use for in-progress operations (file upload, multi-step wizard) where accidental dismissal must be prevented. The `onOpenChange` callback is NOT fired for overlay clicks when this is `false`. **Closes audit gap G-DG1.** |
| `closeOnEscape` | `boolean` | `true` | When `false`, pressing the Escape key does not close the dialog. The `onOpenChange` callback is NOT fired for Escape presses when this is `false`. Pair with `closeOnOverlayClick={false}` for fully explicit-close-only dialogs. |

### 3.1 Portal + scroll lock

Radix's `<Dialog.Portal>` mounts the dialog at the document body level. The
underlying body content is **scroll-locked** while the dialog is open (Radix
default). PAO Styling owns the body scrollbar-gutter compensation.

### 3.2 Overlay

A full-viewport `<Dialog.Overlay>` (`bg-black/40`) sits behind the dialog
content. Clicking the overlay triggers `onOpenChange(false)` by default.
Pass `closeOnOverlayClick={false}` to suppress this — the overlay remains
visible but pointer-down events on it are intercepted (via Radix's
`onPointerDownOutside`) and do not propagate a close request.

### 3.3 Sizing

Current implementation: `max-w-lg` (~32rem / 512px) centred via
`left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2`. Tokens for the size
and centring offsets are owned by PAO Styling.

**Hosts needing a wider dialog must override via `className` in M1.** There
is no `size` prop yet (§8.1 + §7 deferred). Example for a wide data-table
dialog:

```tsx
<Dialog ... className="max-w-3xl">
```

**`size` prop is the top-1 fast-follow request** (closes G-DG2 as a
documented constraint). The planned API for M1.1 is a `size` prop with
values `'sm' | 'md' | 'lg' | 'xl' | 'full'` mirroring Telerik's Small /
Medium / Large / Window.

#### 3.3.1 Body overflow behavior (closes G-DG3)

**When `children` content overflows the dialog's vertical space:**

- The dialog content box **grows vertically** up to a `max-h` limit
  (implementation: `max-h-[85vh]` — ~85% of viewport height).
- When the limit is reached, the **body region scrolls** (`overflow-y: auto`
  on the children wrapper).
- The header (title + close button) and footer remain **sticky** — they do
  not scroll with the body content.
- The page itself is scroll-locked while the dialog is open (Radix default;
  §3.1). The inner body scroll is a separate scroll container and does not
  conflict with the outer scroll-lock.

Hosts placing long forms or data tables inside Dialog should set a reasonable
`min-h` on their content container if they want a consistently tall dialog
rather than one that "pops" open at a small height.

### 3.4 Close button

A close button (`X` icon from `lucide-react`) is rendered in the top-right
of the header. It triggers `<Dialog.Close>` which fires
`onOpenChange(false)`. `aria-label="Close"`. The close button is **always
present** in M1 — there is no `hideCloseButton?: boolean` prop.
**(G-DG4)** Both the close button and the footer `border-t` are
implementation-locked in M1 — neither is parameterized. Telerik's `Dialog`
has `ShowCloseButton=false` for the button case; we do not yet. §7 defers
`hideCloseButton`. For the footer border: if your use case requires footer
actions without a visual separator, render the actions inside `children`
directly and omit the `footer` prop — the border only appears when `footer`
is supplied.

### 3.5 HTML attribute passthrough

Not implemented on the content root or the overlay. Known gap.

### 3.6 ARIA

- `<Dialog.Content>` carries `aria-modal="true"` (set explicitly in
  implementation, redundant with Radix's default but defensive).
- `aria-labelledby` and `aria-describedby` are wired by Radix to the
  `<Dialog.Title>` and `<Dialog.Description>` respectively. PAO
  Accessibility owns the full ARIA matrix.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onOpenChange` | `(open: boolean)` | Radix yields a next open-state. Triggers: close-button click, overlay click, Escape press, or programmatic `<Dialog.Close>` from inside the body. The payload is the next state — `false` for any close gesture, `true` if Radix re-opens (programmatic only — Dialog doesn't open itself). |

There is no separate `onClose` callback — observe `onOpenChange(false)`.
There are no `onClickOutside` / `onEscape` / `onCloseButtonClick`
disambiguations in M1 (deferred per §7).

---

## 5. Slots

| Slot | Purpose | Relationship to props |
| --- | --- | --- |
| `children` | Body content. Required. | Always rendered between header and footer. |
| `footer` | Footer action row. Optional. | Rendered with a top border when supplied; omitted entirely when not. |

No slot for the header (it's driven by `title` / `description` props), no
slot for the close button (always-on `X` icon), no slot for the overlay
(fixed semi-transparent black).

---

## 6. Component composition

### Basic confirmation usage

```typescript
<Dialog
  open={open}
  onOpenChange={setOpen}
  title="Delete invoice"
  description="This will permanently remove the invoice from the system."
  footer={
    <>
      <button onClick={() => setOpen(false)}>Cancel</button>
      <button onClick={handleDelete}>Delete</button>
    </>
  }
>
  Are you sure you want to proceed?
</Dialog>
```

### ConfirmDialog composition

The [ConfirmDialog](./ConfirmDialog.Semantic.md) component is built on top of
Dialog — see that contract for the higher-level wrapper API.

### Form-bearing dialogs

Hosts can render arbitrary content in `children`, including forms. The form's
submission and the dialog's close are independent — the host must call
`onOpenChange(false)` explicitly after a successful submit.

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **`hideCloseButton?: boolean`** — control over whether the `X` button
  renders.
- **`size?: 'sm' | 'md' | 'lg' | 'xl' | 'full'`** — currently fixed at
  `max-w-lg`. Common requests will need this.
- **Custom overlay slot** — for dialogs that want a different overlay
  treatment.
- **`onPointerDownOutside` / `onEscapeKeyDown` passthrough** —
  Added in M1.1 as `closeOnOverlayClick` / `closeOnEscape` props (see §3).
  Raw Radix handler passthrough remains unimplemented.
- **`forceMount`** — keep the portal mounted while `open === false` for
  enter/exit animations driven externally.
- **Nested dialogs** — Radix supports them; the M1 contract is silent and
  the implementation is not tested for nested behaviour.
- **HTML attribute passthrough on the content root.**
- **Drawer / sheet variants** — side-anchored modal surfaces (separate
  component family in the catalog).

---

## 8. Open questions (for council)

1. **Size prop.** `max-w-lg` is fine for short forms but constraining for
   wide-table content or rich editors. Add a `size` prop in M1 or
   fast-follow?
2. **Close-button suppression.** For destructive flows hosts sometimes
   want to remove the close affordance to force a deliberate choice.
   M1 keeps the close button always-on; council to confirm.
3. **`closeOnOverlayClick` / `closeOnEscape` props.** ~~Some flows must suppress
   click-outside-to-close (e.g. uploading; in-flight server work).
   Should the M1 contract expose Radix's hook?~~ **RESOLVED — Added in M1.1
   as `closeOnOverlayClick` and `closeOnEscape` boolean props. Closes audit
   gap G-DG1.**

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
| G-DG1 | Critical | Single `onOpenChange` collapses 3 close gestures; cannot suppress overlay-click | [RESOLVED 2026-06-05] §3 + §8.3: closeOnOverlayClick / closeOnEscape added as M1.1 amendment target; current: all gestures fire onOpenChange(false) |
| G-DG2 | High | Size constraint fixed at max-w-lg | [RESOLVED 2026-06-05] §3.3 + §8.1: size param is M1.1 roadmap; hosts pass className to override |
| G-DG3 | High | Body scroll behavior undocumented | [RESOLVED 2026-06-05] §3 note: body grows; page is scroll-locked (Radix default) |
| G-DG4 | High | Header/footer not parameterized | [RESOLVED 2026-06-05] §3.4: close button always rendered; no ShowCloseButton=false in M1 |
| G-DG5 | Medium | initialFocusRef parity absent | [ACCEPTED-RISK 2026-06-05] §7: Radix default (first focusable); host can use Radix prop-drilling for custom initial focus |
| G-DG6 | Medium | Nested dialogs unsupported | [ACCEPTED-RISK 2026-06-05] §7: nested dialogs unsupported in M1; behaviour undefined |
| G-DG7 | Medium | Form-dialog initial focus: Radix focuses the first focusable element, which is the `×` close button (rendered in `DialogHeader` before the form fields). For form-dialogs, the first form field should receive focus instead. Current G-DG5 mitigation (Radix prop-drilling) requires Radix internals access — not a public API. | Fix-deferred M2 — add `initialFocusSelector?: string` prop (CSS selector for the element that should receive focus on open, e.g. `"[data-autofocus]"` or `"input:first-of-type"`); tracked as RA-10 |
