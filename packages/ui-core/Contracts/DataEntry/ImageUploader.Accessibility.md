# ImageUploader — Accessibility Contract

- **Component:** ImageUploader
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ImageUploader.Semantic.md) · [Interaction](./ImageUploader.Interaction.md) · [Accessibility](./ImageUploader.Accessibility.md) · [Styling](./ImageUploader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ImageUploader.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ImageUploader presents a drag-and-drop zone as a `role="button"`. The hidden
input is excluded from AT. The preview image has a fixed `alt` text. The
remove button has an `aria-label`. The internal error message is a plain `<p>`.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Drop zone `<div>` | `role="button"` | Explicit; `tabIndex={0}` when enabled, `-1` when disabled |
| `<input type="file">` | — | No `aria-hidden` in M1 — known gap (see G1) |
| `<img>` (preview) | `img` (implicit) | `alt="Uploaded preview"` |
| Remove `<button>` | `button` | `aria-label="Remove image"` |
| Upload icon `<div>` with ↑ | decorative | No `aria-hidden` — renders as text in AT (known gap G2) |

---

## 3. Drop zone label

The drop zone has `role="button"` but NO `aria-label`. AT announces it as
an unlabelled button or reads the inner text ("Click or drag to upload image
Max N MB"). This is a gap — an explicit `aria-label` should be added.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Preview image

When value is set, an `<img alt="Uploaded preview">` renders. The alt text
is fixed — it does not describe the actual image content. For M1 this is
acceptable (the host may know the actual filename / description).

**WCAG citation:** WCAG 2.2 SC 1.1.1 Non-text Content.

---

## 5. Remove button

`aria-label="Remove image"` provides an accessible name for the small ✕
button. Focus ring:
```
focus-visible:ring-2 focus-visible:ring-white
```

The white ring is appropriate over the dark semi-transparent overlay, but may
not be visible on light-colored images. A 3:1 contrast of the ring against the
surrounding surface cannot be guaranteed.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible.

---

## 6. Error message

The internal error `<p className="text-xs text-red-600">` is a plain paragraph
— no `role="alert"`. AT does not announce it when it appears. Users who rely
on AT will not hear the "File must be under N MB" message.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 7. Keyboard activation

Drop zone: `Enter` / `Space` → opens file picker.
Remove button: `Enter` / `Space` → removes image (native button).

---

## 8. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Hidden `<input type="file">` lacks `aria-hidden` | Medium | Add `aria-hidden="true"` + `tabIndex={-1}` |
| G2 | ↑ upload icon `<div>` not marked decorative | Low | Add `aria-hidden="true"` |
| G3 | Drop zone has no `aria-label` | High | Add `aria-label="Upload image"` |
| G4 | Error `<p>` has no `role="alert"` — not announced on appearance | High | Add `role="alert"` or use a live region |
| G5 | Remove button focus ring (`focus-visible:ring-white`) may fail contrast on light images | Medium | Use a dark contrasting ring with white outline (e.g. `ring-offset-2 ring-black`) |
