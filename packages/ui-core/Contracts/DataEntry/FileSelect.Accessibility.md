# FileSelect — Accessibility Contract

- **Component:** FileSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileSelect.Semantic.md) · [Interaction](./FileSelect.Interaction.md) · [Accessibility](./FileSelect.Accessibility.md) · [Styling](./FileSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FileSelect presents a native `<button>` as the only focusable element. The
hidden `<input type="file">` is excluded from the AT tree. This gives a clean
keyboard and AT experience for the button activation path.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<button type="button">` | `button` (implicit) | Visible, focusable, accessible name from `text` prop |
| `<input type="file">` | — | `aria-hidden`, `tabIndex={-1}` — excluded from AT tree |
| `FolderIcon` SVG | decorative | `aria-hidden` |

---

## 3. Button label

The button's accessible name is its visible text content (the `text` prop,
default `'Select files…'`). The folder icon is decorative (`aria-hidden`).

**WCAG citations:**
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 2.5.3 Label in Name (visible label matches accessible name)

---

## 4. Focus visible

```
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
```

Focus ring is `ring-2` (2px) using the design system's `--ring` token. This
meets WCAG 2.4.13 Focus Appearance requirements.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible, SC 2.4.13 Focus Appearance.

---

## 5. Disabled state

`disabled` attribute on the button removes it from the Tab order and
announces the button as "dimmed" to AT.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 6. Touch target

| Size | Height | WCAG 2.5.8 status |
|---|---|---|
| `small` | `h-7` (~28px) | Meets 24×24 minimum |
| `medium` | `h-9` (~36px) | Meets 24×24 minimum |
| `large` | `h-11` (~44px) | Meets 24×24 minimum |

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | No live region announces selected file count after picking | Low | Add `aria-live="polite"` region with post-selection summary |
