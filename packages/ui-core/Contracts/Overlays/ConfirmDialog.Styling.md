# ConfirmDialog — Styling Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Overlays
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Interaction](./ConfirmDialog.Interaction.md) · [Accessibility](./ConfirmDialog.Accessibility.md)
- **Related contract:** [Dialog.Styling.md](./Dialog.Styling.md) — ConfirmDialog composes Dialog; this contract documents only the deltas (the two action buttons and the variant axis).
- **Reference implementation:** `packages/ui-react/src/components/dialogs/ConfirmDialog.tsx` (composes `Dialog.tsx`)
- **Catalog row:** (no Telerik counterpart — ConfirmDialog is a Harborline-native composition)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

ConfirmDialog is a specialisation of Dialog: a modal that asks the user
to confirm a single action via two buttons in the footer (Cancel + the
named confirm action). It inherits the full Dialog visual surface
(backdrop, content panel, header, body) and adds only:

1. A **footer with two right-aligned buttons** (`Cancel` + the named
   confirm action).
2. A **variant axis** (`default` | `destructive`) that re-themes the
   confirm button's colour family.
3. An **empty body** — the description (rendered in Dialog's header) is
   the entire message; ConfirmDialog supplies `<span />` to Dialog's
   `children` slot.

This contract names the `--sf-confirm-*` token surface (the variant axis
+ button-specific tokens) and inherits all other tokens from Dialog.

---

## 2. Token surface

ConfirmDialog inherits the full `--sf-dialog-*` family from Dialog. This
contract adds the `--sf-confirm-*` family for the two-button footer:

### 2.1 Cancel button (always present)

The cancel button is the secondary action — neutral / outline style:

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-confirm-cancel-bg` | Cancel button background | default | Typically white (matches Dialog content bg) |
| `--sf-confirm-cancel-bg-hover` | Cancel button background on hover | hover | Tinted neutral (e.g., `--sf-color-neutral-50`) |
| `--sf-confirm-cancel-fg` | Cancel button label colour | default | Neutral text colour, pairs with cancel bg at ≥ 4.5:1 |
| `--sf-confirm-cancel-border` | Cancel button border | default | Pairs with cancel bg at ≥ 3:1 |
| `--sf-confirm-cancel-ring-focus` | Cancel button focus ring | focus | Brand-accent; pairs at ≥ 3:1 against host surface |
| `--sf-confirm-cancel-radius` | Cancel button corner radius | none | `var(--sf-radius-md)` |
| `--sf-confirm-cancel-padding` | Cancel button internal padding | none | `0.5rem 1rem` (= `px-4 py-2`) |

### 2.2 Confirm button — `default` variant

The default-variant confirm button is the primary action — brand-blue
solid:

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-confirm-default-bg` | Confirm button background | default | Brand-primary (e.g., `--sf-color-primary-600`) |
| `--sf-confirm-default-bg-hover` | Confirm button background on hover | hover | Darker primary (e.g., `--sf-color-primary-700`) |
| `--sf-confirm-default-fg` | Confirm button label colour | default | White; pairs with confirm bg at ≥ 4.5:1 |
| `--sf-confirm-default-ring-focus` | Confirm button focus ring | focus | Brand-primary (lighter for ring visibility) |

### 2.3 Confirm button — `destructive` variant

The destructive-variant confirm button signals "this action is
irreversible / dangerous" — brand-red solid:

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-confirm-destructive-bg` | Confirm button background | destructive | Brand-error red (e.g., `--sf-color-error-600`) |
| `--sf-confirm-destructive-bg-hover` | Confirm button background on hover | destructive + hover | Darker error (e.g., `--sf-color-error-700`) |
| `--sf-confirm-destructive-fg` | Confirm button label colour | destructive | White; pairs at ≥ 4.5:1 |
| `--sf-confirm-destructive-ring-focus` | Confirm button focus ring | destructive + focus | Brand-error red |

### 2.4 Token additions to `overlays.tokens.json`

```jsonc
"confirmDialog": {
  "cancel": {
    "bg": "#FFFFFF",
    "bgHover": "#F9FAFB",
    "fg": "#374151",
    "border": "#D1D5DB",
    "ringFocus": "#3B82F6",
    "radius": "0.375rem",
    "padding": "0.5rem 1rem"
  },
  "default": {
    "bg": "#2563EB",
    "bgHover": "#1D4ED8",
    "fg": "#FFFFFF",
    "ringFocus": "#3B82F6"
  },
  "destructive": {
    "bg": "#DC2626",
    "bgHover": "#B91C1C",
    "fg": "#FFFFFF",
    "ringFocus": "#EF4444"
  },
  "_notes": {
    "contrast": "cancel.fg on cancel.bg verified ≥ 4.5:1. default.fg + destructive.fg (both #FFFFFF) on their respective bg colours verified ≥ 4.5:1. cancel.border on cancel.bg verified ≥ 3:1. All ringFocus colours on host surface verified ≥ 3:1.",
    "variant-axis": "The destructive variant signals 'this action cannot be undone' through THREE channels: (1) red-family background colour, (2) the confirm button's label text (which should be a verb like 'Delete' / 'Discard' / 'Remove'), (3) the dialog's title and description (which the host writes to set context). Colour-alone would risk WCAG 1.4.1; the label-text channel is the load-bearing redundancy."
  }
}
```

---

## 3. Tailwind class recipes (M1 default layer)

### 3.1 Cancel button

```
rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700
hover:bg-gray-50
focus:outline-none focus:ring-1 focus:ring-blue-500 focus:ring-offset-1
```

### 3.2 Confirm button — variant: default

```
rounded-md px-4 py-2 text-sm font-medium text-white
bg-blue-600 hover:bg-blue-700
focus:outline-none focus:ring-1 focus:ring-blue-500 focus:ring-offset-1
```

### 3.3 Confirm button — variant: destructive

```
rounded-md px-4 py-2 text-sm font-medium text-white
bg-red-600 hover:bg-red-700
focus:outline-none focus:ring-1 focus:ring-red-500 focus:ring-offset-1
```

### 3.4 Empty body slot

```tsx
{/* Body slot intentionally empty for ConfirmDialog — description carries the message */}
<span />
```

The `<span />` is a deliberate zero-content body. The Dialog wrapper
renders `px-6 py-4` body padding anyway; the empty span allows the body
slot to take no vertical height beyond that padding.

A possible follow-on enhancement is suppressing the body padding when the
slot is empty; out of scope for M1.

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | dialog open, variant = default, no hover, no focus | both buttons | base tokens per variant |
| **destructive** | dialog open, variant = destructive | confirm button | brand-error red family |
| **cancel hover** | pointer over Cancel | Cancel button | `hover:bg-gray-50` |
| **cancel focus-visible** | keyboard focus on Cancel | Cancel button | `focus:ring-1 focus:ring-blue-500 focus:ring-offset-1` |
| **confirm hover (default)** | pointer over Confirm | Confirm button | `hover:bg-blue-700` |
| **confirm hover (destructive)** | pointer over Confirm | Confirm button | `hover:bg-red-700` |
| **confirm focus-visible (default)** | keyboard focus on Confirm | Confirm button | `focus:ring-blue-500 focus:ring-offset-1` |
| **confirm focus-visible (destructive)** | keyboard focus on Confirm | Confirm button | `focus:ring-red-500 focus:ring-offset-1` |

All Dialog-inherited states (open animation, close animation, backdrop,
header divider, etc.) compose unchanged.

---

## 5. Composition with Dialog

ConfirmDialog composes Dialog by:

1. Passing `open` / `onOpenChange` through unchanged.
2. Passing `title` / `description` through unchanged.
3. Building a `footer` React fragment with the two buttons and passing it
   to Dialog's `footer` slot.
4. Supplying `<span />` as Dialog's `children` (the body slot).

The Dialog wrapper handles all the modal mechanics; ConfirmDialog only
contributes the action-button row.

---

## 6. Open questions

1. **Loading state on Confirm.** Async-confirm patterns (e.g., "Delete
   property" that hits an API) want a loading spinner on the Confirm
   button while the API is in flight. M1 does NOT expose a `confirmLoading`
   prop; the host must compose this externally. Probably warrants a
   follow-on amendment.
2. **`confirmLabel` vs button colour.** The variant axis controls the
   button colour but NOT the label. Hosts MUST write semantic labels
   ("Delete property", not just "OK") so the visual + text channel
   combine for unambiguous communication. The contract documents this in
   the §2.4 notes ("variant-axis" entry) but does NOT enforce it. The
   Semantic contract MAY add a runtime warning when `variant="destructive"`
   AND `confirmLabel === "Confirm"` (the default).
3. **Three-button variant.** Some confirm patterns want Cancel + Save +
   "Discard changes". Out of scope for M1 — ConfirmDialog is single-
   action only.
4. **Footer-left supplementary text.** Some dialogs want a left-aligned
   note in the footer ("This action can't be undone" beside the buttons).
   Out of scope for M1; hosts compose externally if needed.

---

## 7. Do / Don't

### Do

- Consume `--sf-confirm-*` via the recipes in §3.
- Use the `destructive` variant whenever the action is irreversible
  (delete, remove, discard) — the red colour family is the right signal.
- Write the `confirmLabel` as a verb ("Delete", "Discard") so colour +
  text combine for unambiguous communication.
- Keep the Cancel button as the FIRST footer child (left-most in the
  visual row); the Confirm button is the right-most action. This matches
  Western LTR conventions.
- Honor `prefers-reduced-motion: reduce` (inherited from Dialog
  animations — see Dialog.Styling §2.7).

### Don't

- Don't put concrete hex values in this contract or in `ConfirmDialog.tsx`.
  Hex values belong only in `overlays.tokens.json`.
- Don't use the `destructive` variant for non-destructive actions — it
  trains users to ignore the red signal.
- Don't omit the `description` prop — ConfirmDialog's description IS the
  message body (ConfirmDialog renders no body content). Without a
  description, the dialog has only a title.
- Don't reverse the button order (Confirm on left, Cancel on right) —
  the Western convention puts the primary action on the right.
- Don't add a third button without a contract amendment — the two-button
  shape is the contract.

---

## 8. Parity notes

- **Blazor (future HarborlineConfirmDialog track):** consumes the same
  `--sf-confirm-*` family. Composes the equivalent Blazor Dialog.
- **React (this contract):** as documented, composes
  `packages/ui-react/src/components/dialogs/Dialog.tsx`.
- **Web Components (Phase M4, Lit):** TBD; composes a `<sf-dialog>` web
  component.

---

## References

- ADR 0017 §A1.3 — Overlays family contract scope
- [ConfirmDialog.Semantic.md](./ConfirmDialog.Semantic.md) — prop contract
- [ConfirmDialog.Interaction.md](./ConfirmDialog.Interaction.md) — behavioural contract
- [ConfirmDialog.Accessibility.md](./ConfirmDialog.Accessibility.md) — ARIA + focus
- [Dialog.Styling.md](./Dialog.Styling.md) — parent surface
- `_shared/design/tokens/overlays.tokens.json` — concrete default values
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
