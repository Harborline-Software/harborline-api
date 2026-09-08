# Window — Accessibility Contract

- **Component:** Window
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted (shipped baseline) + Draft (expansion sections §FS-*)
- **Companion contracts:** [Semantic](./Window.Semantic.md) · [Interaction](./Window.Interaction.md) · [Styling](./Window.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Window.tsx`
- **Catalog row:** #149 Window (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

Window's accessibility contract has two jobs in M1:

1. **Pin the shipping baseline.** The implementation today emits `aria-label`
   on the three control buttons. This contract documents those emissions so
   they don't regress.
2. **Name the M1 gaps honestly.** Window is a WAI-ARIA dialog/window pattern
   with required ARIA roles and keyboard behaviors not yet shipped. Each gap
   is listed with its deferred-feature link.

---

## 2. ARIA structural roles (Accepted)

| Element | Current M1 emission | Target (WAI-ARIA dialog/window) | Status |
|---|---|---|---|
| Window container `<div>` | no ARIA role; no `aria-labelledby` | `role="dialog"` + `aria-labelledby={titleId}` + `aria-modal={modal}` | **Gap — §FS-1.1** |
| Title bar `<div>` | no ARIA role | `role="heading"` on the title span (or `aria-labelledby` linkage) | **Gap — §FS-1.1** |
| Title `<span>` | no id | `id={titleId}` for `aria-labelledby` | **Gap — §FS-1.1** |
| Minimize `<button>` | `aria-label="Minimize"` | `aria-label="Minimize"` | **Parity** |
| Maximize `<button>` | `aria-label="Maximize"` | `aria-label="Maximize"` (default) / `aria-label="Restore"` (when maximized) | **Partial** — label does not change on state |
| Close `<button>` | `aria-label="Close"` | `aria-label="Close"` | **Parity** |
| Backdrop `<div>` | no ARIA attributes | `aria-hidden="true"` (decorative) | **Gap — §FS-1.2** |
| Body `<div>` | no ARIA role | no role (plain content region) | **Acceptable** — role="dialog" on container covers the content |

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships + SC 4.1.2 Name, Role, Value.

---

## 3. Button labels (Accepted)

The three control buttons are native `<button type="button">` elements. All three
carry `aria-label` in M1:

| Button | `aria-label` | Shipping state |
|---|---|---|
| Minimize | `"Minimize"` | Shipped — tested in `Window.test.tsx` §2 |
| Maximize | `"Maximize"` | Shipped — tested §2; label does NOT change to "Restore" when maximized (gap §FS-1.1) |
| Close | `"Close"` | Shipped — tested §2 |

The icon characters (`—`, `□`, `❐`, `×`) are not sufficient accessible names on
their own; the `aria-label` attributes are the accessible names.

---

## 4. Focus management gaps (M1)

| Gap | Description | WCAG criterion | Target |
|---|---|---|---|
| **No focus trap** | Window does not trap focus. Tab cycles through the document normally; pressing Tab inside the window can exit to background content. For `modal === true` this is a Level A failure. | WCAG 2.1 SC 2.1.2 No Keyboard Trap (the inverse — keyboard MUST be trappable when modal) | §FS-1.3 |
| **No auto-focus on mount** | The window container does not receive focus when mounted. Screen-reader users are not notified of the new dialog. | WCAG 2.2 SC 3.2.5 Change on Request (informational; not a hard failure at current priority) | §FS-1.4 (auto-focus prop, Semantic §FS-2.2) |
| **No return-focus on close** | Focus does not return to the element that triggered the window when it is closed. | WCAG 2.1 SC 2.4.3 Focus Order | §FS-1.5 |
| **No Escape key close** | Escape does not close the window; it is required by WAI-ARIA Dialog pattern. | WCAG 2.1 SC 2.1.1 Keyboard | §FS-1.6 (Interaction §FS-3.2) |

---

## 5. Keyboard navigation (M1 shipping)

The only keyboard-accessible elements inside the window in M1 are the three
control buttons (all native `<button>` elements; fully keyboard-accessible
via Tab / Enter / Space). No keyboard-driven move or resize is shipped in M1.

---

## 6. Touch target size (Accepted)

The Styling contract §2.4–2.6 specifies the control buttons at `h-6 w-6` (24×24 px),
which meets the WCAG 2.2 SC 2.5.8 (Level AA) minimum target size of 24×24 px.

**NOTE:** The shipping implementation uses `h-5 w-5` (20×20 px), which is below
the minimum. The Styling contract documents the corrected size. This is a **known
gap** between the Styling contract and the shipping implementation. The
implementation must be updated to `h-6 w-6` to close this gap.

---

## 7. Deferred accessibility features (M1)

- `role="dialog"` + `aria-labelledby` + `aria-modal` on the window container
- Dynamic `aria-label="Restore"` on Maximize button when state is maximized
- `aria-hidden="true"` on the backdrop
- Focus trap for modal mode
- Auto-focus on mount (WCAG-required for modal dialogs)
- Return-focus on close
- Keyboard-driven move and resize (Arrow keys)
- Escape key to close or restore

---

## Full-surface expansion (Draft — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — ARIA wiring

#### §FS-1.1 `role="dialog"` + `aria-labelledby`

The window container must announce itself to assistive technology as a dialog.

**Required changes:**
1. Generate a stable `id` for the title `<span>`: `const titleId = React.useId()`.
2. Add to the window container `<div>`:
   - `role="dialog"`
   - `aria-labelledby={titleId}`
   - `aria-modal={modal ? 'true' : undefined}` — `aria-modal` scopes the virtual
     cursor to the dialog in modal mode; omit when `modal === false` to avoid
     confusing AT when the window is non-modal.
3. Add `id={titleId}` to the title `<span>`.

**WCAG citation:** WCAG 2.2 SC 4.1.2 + WAI-ARIA `dialog` role authoring practices.

**wave-2**

---

#### §FS-1.2 Backdrop `aria-hidden`

The modal backdrop is decorative. It MUST carry `aria-hidden="true"` to prevent
AT from announcing or navigating to the backdrop div.

**Required change:** add `aria-hidden="true"` to the backdrop `<div>` when
`modal === true`.

**wave-2**

---

#### §FS-1.3 Focus trap for modal mode

When `modal === true`, the window MUST trap keyboard focus within the window
container. The user cannot Tab out to background content.

**Implementation approach:**
- Apply a focus-trap logic (either a minimal hand-rolled implementation trapping
  Tab/Shift+Tab on the window's first/last focusable element, or a small library
  such as `focus-trap-react`).
- The focus trap is ONLY active when `modal === true`. Non-modal windows do NOT
  trap focus (standard windowing UX).
- Focus trap must handle `inert` attribute polyfill for older browsers if the
  `inert` approach is chosen; `focus-trap` library is preferred for reliability.

**WCAG citation:** WCAG 2.1 SC 2.1.2 + WAI-ARIA dialog pattern.

**wave-2**

---

#### §FS-1.4 Auto-focus on mount

Paired with Semantic §FS-2.2 (`autoFocus` prop).

**Implementation approach:**
1. The window container `<div>` receives `tabIndex={-1}` (makes it programmatically
   focusable without inserting it in the Tab order).
2. In a `useEffect(() => { if (autoFocus) containerRef.current?.focus() }, [])`,
   focus moves to the container (or the first focusable child, if the browser
   follows normal focus-first-focusable for elements with `tabIndex=-1`).
3. Screen-reader: the `role="dialog"` + `aria-labelledby` causes AT to announce
   the dialog title when focus enters.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages + WAI-ARIA dialog open
focus-management practice.

**wave-2**

---

#### §FS-1.5 Return-focus on close

When the window is closed (via Close button, Escape, or programmatic `state`
prop removal), focus MUST return to the element that was focused before the
window was opened.

**Implementation approach:**
1. On mount, capture `document.activeElement` in a ref: `triggerRef.current = document.activeElement as HTMLElement`.
2. On unmount (`useEffect` cleanup), call `triggerRef.current?.focus()`.
3. In controlled mode the unmount is triggered by the host removing `<Window>`
   from the tree after handling `onClose` — the cleanup fires at unmount time
   regardless of the trigger.

**WCAG citation:** WCAG 2.1 SC 2.4.3 Focus Order.

**wave-2**

---

#### §FS-1.6 Escape key to close / restore

See Interaction §FS-3.2 for the behavioral specification.

**ARIA note:** the WAI-ARIA dialog pattern requires Escape to dismiss the dialog.
A Window with `closable === true` MUST respond to Escape with the same effect as
clicking Close. When `closable === false`, Escape is suppressed.

**WCAG citation:** WCAG 2.1 SC 2.1.1 Keyboard + WAI-ARIA dialog Escape behavior.

**wave-2** (aligned with Interaction §FS-3.2)

---

### §FS-2 Wave-3 — dynamic ARIA + keyboard

#### §FS-2.1 Dynamic Maximize / Restore button label

The Maximize button's `aria-label` MUST change to `"Restore"` when the window is
in `'maximized'` state, so screen-reader users understand the button's current action.

**Required change:**
```typescript
aria-label={isMaximized ? 'Restore' : 'Maximize'}
```

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — the accessible name
must describe the button's current action.

**wave-3**

---

#### §FS-2.2 Keyboard move — `tabIndex` on title bar

See Interaction §FS-3.1 for the behavioral specification.

**ARIA requirements:**
1. The title bar `<div>` gains `tabIndex={0}` when `draggable === true`, making
   it keyboard-focusable for Arrow-key move operations.
2. Add `aria-roledescription="Move handle"` or `role="group"` +
   `aria-label="Move {title}"` to the title bar to communicate its function to
   screen-readers. (WAI-ARIA does not have a specific "move handle" role;
   `aria-description="Press arrow keys to move window"` is a usable fallback.)
3. When the title bar is focused, a visible focus ring MUST be present (Styling
   contract §FS-2 specifies the focus ring token).

**WCAG citation:** WCAG 2.1 SC 2.4.7 Focus Visible + SC 2.1.1 Keyboard.

**wave-3**

---

#### §FS-2.3 Keyboard resize — resize handle ARIA

See Interaction §FS-3.1 for the behavioral specification.

**ARIA requirements:**
1. Each resize handle `<div>` gains `tabIndex={0}` and `role="separator"`.
2. `aria-orientation`:
   - SE corner: omit (diagonal; not a clean single-axis separator)
   - S edge: `aria-orientation="horizontal"`
   - E edge: `aria-orientation="vertical"`
3. `aria-label`:
   - SE: `aria-label="Resize from bottom-right corner"`
   - S: `aria-label="Resize height from bottom edge"`
   - E: `aria-label="Resize width from right edge"`
4. `aria-valuenow` / `aria-valuemin` / `aria-valuemax`:
   - S handle: `aria-valuenow={size.height}` / `aria-valuemin={minHeight}` / `aria-valuemax` = omitted (no maximum in M1)
   - E handle: `aria-valuenow={size.width}` / `aria-valuemin={minWidth}` / `aria-valuemax` = omitted

**WCAG citation:** WAI-ARIA `separator` role + `aria-valuenow` pattern for
resizable separators.

**wave-3**
