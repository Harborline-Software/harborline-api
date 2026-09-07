# ConfirmDialog — Semantic Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ConfirmDialog.Interaction.md) · [Accessibility](./ConfirmDialog.Accessibility.md) · [Styling](./ConfirmDialog.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConfirmDialog.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — extends Dialog (which wraps `@radix-ui/react-dialog`)

---

## 1. Purpose

ConfirmDialog is a **lightweight modal confirmation gate** used before
irreversible or high-impact actions (delete, archive, send, apply). It
renders a centered overlay dialog with a title, optional description, and
two buttons: a cancel (safe default) and a confirm (destructive or default).

ConfirmDialog vs related components:

| Criterion | ConfirmDialog | Alert |
| --- | --- | --- |
| Persistence | Modal — blocks interaction | Inline — in page flow |
| Placement | Fixed viewport overlay | Inline in page flow |
| User gate | Yes — requires choice before proceeding | No |
| Typical context | Delete confirmation, discard changes | Page-level error, advisory |

**Primary Harborline use cases:**

- "Are you sure you want to delete this vendor?" (danger variant)
- "Submit this invoice for approval?" (default variant)
- "Discard unsaved changes?" (danger variant)

---

## 2. Data model

ConfirmDialog is controlled — `open` state is owned by the host.

```typescript
type ConfirmVariant = 'default' | 'danger'

interface ConfirmDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description?: string
  confirmLabel?: string
  cancelLabel?: string
  variant?: ConfirmVariant
  loading?: boolean
  onConfirm: () => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `open` | `boolean` | _required_ | Controls whether the dialog is mounted and visible. |
| `onOpenChange` | `(open: boolean) => void` | _required_ | Called when the dialog requests a state change (backdrop click, Escape key, Cancel button). Host updates `open`. |
| `title` | `string` | _required_ | The dialog heading. Bound to `aria-labelledby`. |
| `description` | `string` | — | Optional supporting text below the title. Bound to `aria-describedby` when present. |
| `confirmLabel` | `string` | `'Confirm'` | Label for the confirm action button. |
| `cancelLabel` | `string` | `'Cancel'` | Label for the cancel button. |
| `variant` | `'default' \| 'danger'` | `'default'` | Determines confirm button color and presence of warning icon. |
| `loading` | `boolean` | `false` | When true, both buttons are disabled and a spinner appears in the confirm button. |
| `onConfirm` | `() => void` | _required_ | Called when the user activates the confirm button. |

### 3.1 Variant semantics

| `variant` | Semantic intent | Confirm button | Warning icon |
| --- | --- | --- | --- |
| `default` | Neutral confirmation (submit, apply) | Blue | None |
| `danger` | Destructive action (delete, discard) | Red | Red triangle alert icon |

### 3.2 Controlled visibility

ConfirmDialog renders `null` when `open` is `false`. The host is responsible
for mounting/unmounting by toggling `open`. This keeps the component stateless
with respect to visibility.

### 3.3 `loading` state

When `loading={true}`, both the cancel and confirm buttons are disabled
(`disabled` attribute). The confirm button renders an animated spinner SVG
alongside the label to indicate an in-progress async operation. This prevents
double-submission.

### 3.4 Backdrop click

Clicking the backdrop overlay fires `onOpenChange(false)`, giving the host the
opportunity to close. This matches the Escape key behavior.

---

## 4. Events — semantics

| Event | Signature | Trigger |
| --- | --- | --- |
| `onConfirm` | `() => void` | User clicks the confirm button (when not `loading`). |
| `onOpenChange(false)` | `(open: boolean) => void` | Cancel button click, Escape key, or backdrop click. |
| `onOpenChange(true)` | — | Not fired by ConfirmDialog itself — the host opens it. |

---

## 5. Composition

### Delete confirmation

```tsx
const [open, setOpen] = useState(false)
const [deleting, setDeleting] = useState(false)

async function handleDelete() {
  setDeleting(true)
  await deleteVendor(id)
  setDeleting(false)
  setOpen(false)
}

<Button variant="destructive" onClick={() => setOpen(true)}>Delete</Button>
<ConfirmDialog
  open={open}
  onOpenChange={setOpen}
  title="Delete vendor?"
  description="This action cannot be undone. All associated records will be removed."
  confirmLabel="Delete vendor"
  variant="danger"
  loading={deleting}
  onConfirm={handleDelete}
/>
```

### Submit confirmation (default variant)

```tsx
<ConfirmDialog
  open={open}
  onOpenChange={setOpen}
  title="Submit invoice for approval?"
  description="Once submitted, you cannot edit this invoice until it is returned."
  confirmLabel="Submit"
  onConfirm={handleSubmit}
/>
```

---

## 6. Deferred features

- **`onCancel` callback** — currently Cancel fires `onOpenChange(false)` only;
  a separate `onCancel` would allow hosts to distinguish cancel from backdrop
  dismiss.
- **Custom dialog width** — dialog is fixed `max-w-md`; a `size` prop
  (`'sm' | 'md' | 'lg'`) is not yet supported.
- **Multiple action buttons** — today exactly one confirm + one cancel;
  three-option dialogs (e.g., "Save", "Discard", "Cancel") are not supported.
- **Focus trap** — on `open`, focus moves to the cancel button; Tab does not
  cycle within the dialog. A full focus trap is deferred.
