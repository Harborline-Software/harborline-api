# ConfirmDialog — Semantic Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ConfirmDialog.Interaction.md) · [Styling](./ConfirmDialog.Styling.md) · [Accessibility](./ConfirmDialog.Accessibility.md)
- **Composes:** [Dialog.Semantic.md](./Dialog.Semantic.md) — ConfirmDialog is a thin wrapper around Dialog.
- **Reference implementation:** `packages/ui-react/src/components/dialogs/ConfirmDialog.tsx`
- **Catalog row:** (no Telerik counterpart — ConfirmDialog is a Harborline-native composition)
- **App-priority:** `high`
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — extends Dialog (which wraps `@radix-ui/react-dialog`)

---

## 1. Purpose

ConfirmDialog is a higher-level wrapper around [Dialog](./Dialog.Semantic.md)
that bundles the standard "confirm vs cancel" footer pattern. Hosts that need
a yes/no confirmation flow (delete, discard, archive, sign out) use
ConfirmDialog instead of authoring Dialog + footer buttons by hand.

The wrapper:

- Renders pre-built Cancel and Confirm buttons in the footer.
- Closes the dialog automatically when either button is clicked.
- Offers a `variant` prop that picks the Confirm button colour (`default` =
  blue; `destructive` = red).

ConfirmDialog is **not** suitable when the host needs a body content area —
it intentionally renders an empty body (the description carries the
message). For richer confirmation flows (e.g. a confirmation with a checkbox
"Don't ask again"), use Dialog directly.

---

## 2. Data model

ConfirmDialog is fully controlled — same open-state pattern as Dialog.

```typescript
interface ConfirmDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description: string                 // REQUIRED (carries the question)
  confirmLabel?: string               // default "Confirm"
  cancelLabel?: string                // default "Cancel"
  onConfirm: () => void
  variant?: 'default' | 'destructive' // default 'default'
  closeOnOverlayClick?: boolean       // default: true
  closeOnEscape?: boolean             // default: true
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `open` | `boolean` | _required_ | Controlled open state. Passes through to underlying Dialog. |
| `onOpenChange` | `(open: boolean) => void` | _required_ | Fires when Radix wants to change the open state — close-button click, overlay click, Escape, Cancel, or Confirm. Passes through to underlying Dialog. |
| `title` | `string` | _required_ | Dialog title. |
| `description` | `string` | _required_ | The confirmation question / explanation. ConfirmDialog mandates `description` (unlike base Dialog where it's optional) because the message is the dialog's whole purpose. |
| `confirmLabel` | `string` | `'Confirm'` | Label for the primary action button. Hosts typically override (`'Delete'`, `'Archive'`, `'Sign out'`). |
| `cancelLabel` | `string` | `'Cancel'` | Label for the cancel button. |
| `onConfirm` | `() => void` | _required_ | Fires when the user activates the Confirm button. ConfirmDialog also calls `onOpenChange(false)` immediately after — so hosts don't need to close manually. |
| `variant` | `'default' \| 'destructive'` | `'default'` | Visual variant for the Confirm button. `destructive` renders red (for delete-style flows); `default` renders blue (for neutral confirmations). |
| `closeOnOverlayClick` | `boolean` | `true` | Passed through to underlying Dialog. When `false`, clicking the overlay does not close the dialog. Does NOT affect the Confirm/Cancel button auto-close. **Added in M1.1. Closes audit gap G-DG1.** |
| `closeOnEscape` | `boolean` | `true` | Passed through to underlying Dialog. When `false`, pressing Escape does not close the dialog. Does NOT affect the Confirm/Cancel button auto-close. **Added in M1.1.** |

### 3.1 `description` is required

Base Dialog allows `description` to be omitted; ConfirmDialog requires it.
The empty-body design choice depends on the description carrying the
confirmation question — without it, the dialog has only the title and the
two buttons, which is too sparse for the typical confirmation use case.

### 3.2 Empty body

ConfirmDialog renders an empty `<span />` as Dialog's `children`. The
description prop sits in the Dialog header (per Dialog.Semantic §3). There is
no way to add body content via ConfirmDialog — use Dialog directly.

### 3.3 Auto-close on Confirm

When the Confirm button is activated, ConfirmDialog calls `onConfirm()`
**then** `onOpenChange(false)`. The dialog closes regardless of what
`onConfirm()` does (no exception handling, no async await). Hosts that
need the dialog to stay open during an in-flight async confirm (e.g. a
deletion that takes 2 seconds) must keep state of the in-flight operation
outside the dialog and either:

- Re-open the dialog in an "in-progress" state if it needs to show a
  spinner; OR
- Render a Toast / status banner separately to report success/failure.

**WARNING — ConfirmDialog is NOT safe for async operations that may fail
without an in-dialog error state.** If `onConfirm` calls `await api.delete(id)`
and the request fails after the dialog has already closed, the user sees no
error in context. The canonical pattern:

```tsx
const [confirmOpen, setConfirmOpen] = useState(false)

async function handleConfirm() {
  // Dialog closes immediately after this returns.
  // Fire-and-forget; handle failure in the toast layer.
  api.deleteInvoice(id)
    .then(() => notify.success('Invoice deleted'))
    .catch(() => notify.error('Delete failed — please try again'))
}

<ConfirmDialog
  open={confirmOpen}
  onOpenChange={setConfirmOpen}
  title="Delete invoice"
  description="This cannot be undone."
  confirmLabel="Delete"
  variant="destructive"
  onConfirm={handleConfirm}
/>
```

Do NOT `await` the operation inside `onConfirm` and rely on the dialog
staying open to show a spinner — the dialog is already closed by the time
the await resolves. Use a `loading` state with a separate spinner (or
the deferred `loading?: boolean` prop in §7) if you need in-dialog async
feedback. **Closes audit gap G-CD1.**

A `loading?: boolean` prop to keep the dialog open during async work is
deferred (§7).

### 3.4 Variant — `destructive`

When `variant === 'destructive'`, the Confirm button renders red
(`bg-red-600 hover:bg-red-700 focus:ring-red-500`). The Cancel button is
unaffected. Tokens for the destructive treatment are owned by PAO Styling.

**`variant='warning'` is not in M1.** Property-management flows commonly
want a "warning but not destructive" variant (amber/yellow — "This will
email the tenant — proceed?"). The `destructive` red overstates the
consequence; `default` blue understates it. A `'warning'` middle variant
is tracked as a fast-follow (council OQ §8.3). Until then, hosts should
use `variant='default'` for reversible-but-notable actions and add a
warning icon or text in the `description` prop. **Closes audit gap G-CD2.**

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onConfirm` | `()` | The user activates the Confirm button (click or Enter/Space when focused). Fires before the auto-close. |
| `onOpenChange` | `(open: boolean)` | Any close gesture — Cancel click, Confirm click (auto-close), Dialog's own close routes (close-button, overlay click, Escape). The payload is the next state. |

**Cancel ALWAYS closes the dialog.** There is no `onCancel` callback and
no way to suppress the Cancel-close. The Cancel button fires
`onOpenChange(false)` directly — hosts cannot intercept and prevent the
close. If you need a "cancel with confirmation" pattern (e.g. "unsaved
changes — are you sure you want to cancel?"), use Dialog directly.
**Closes audit gap G-CD3.**

There is no separate `onCancel` callback. To distinguish Cancel from the
other close routes, hosts observe `onOpenChange(false)` and check whether
`onConfirm` was called in the same tick. In practice almost no host needs
this distinction.

---

## 5. Slots

ConfirmDialog has **no slot extensibility** in M1. Title, description, and
button labels are all `string` props; footer is internally constructed; body
is fixed-empty. Slots are deferred (§7).

---

## 6. Component composition

ConfirmDialog composes Dialog. The composition is intentionally rigid —
ConfirmDialog is the "standard yes/no" flow; everything else uses Dialog
directly.

### Typical use

```typescript
<ConfirmDialog
  open={confirmDelete}
  onOpenChange={setConfirmDelete}
  title="Delete invoice"
  description="This will permanently remove invoice INV-2026-06-04-0001. This action cannot be undone."
  confirmLabel="Delete"
  variant="destructive"
  onConfirm={async () => {
    await api.deleteInvoice(id)
    // ConfirmDialog closes automatically; host shows a toast separately.
  }}
/>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **`loading?: boolean`** — keep open during in-flight async work; show
  spinner on the Confirm button.
- **Custom button slots** — for hosts who want to render entirely custom
  buttons but reuse the wrapper.
- **Body slot** — render an explanatory paragraph or a "Don't show again"
  checkbox.
- **Third button** ("Discard" + "Cancel" + "Save"-style triads).
- **Inline icon** in the title or description (e.g. a destructive-warning
  icon).
- **`onCancel?: () => void`** — explicit callback for the Cancel button
  alone.
- **Promise-returning `onConfirm`** with built-in error handling.

---

## 8. Open questions (for council)

1. **`loading` prop.** This is a near-universal request the moment hosts
   try ConfirmDialog with a real async API. Bake it in M1 or fast-follow?
   (Leaning: fast-follow once we see real call sites.)
2. **Explicit `onCancel` callback.** Today hosts observe
   `onOpenChange(false)`. Cheap to add a dedicated callback for clarity.
3. **Variant taxonomy.** `default` / `destructive` is fine for M1. A
   `warning` variant (yellow / amber) might be useful for "this might
   not be what you want" flows. Council to confirm vocabulary.

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
| G-CD1 | Critical | `loading` async-confirm: dialog closes before async completes | [RESOLVED 2026-06-05] §3.3 WARNING block: do NOT use ConfirmDialog for async work that may fail; show a separate Notification on error |
| G-CD2 | High | `warning` variant open question unresolved | [RESOLVED 2026-06-05] §3.4: warning variant (amber) added to roadmap; §8.3 noted |
| G-CD3 | High | Cancel always closes — implicit, not explicit | [RESOLVED 2026-06-05] §4: Cancel always fires onOpenChange(false); no onCancel prop; no host action required |
| G-CD4 | Medium | `confirmLabel` default "Confirm" wrong for destructive | [ACCEPTED-RISK 2026-06-05] §3: hosts SHOULD override confirmLabel for destructive actions |
| G-CD5 | Medium | Body slot deferred; composition recipe absent | [ACCEPTED-RISK 2026-06-05] §7: use Dialog directly for body-slot cases |
