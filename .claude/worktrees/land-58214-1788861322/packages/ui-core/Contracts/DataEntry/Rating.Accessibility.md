# Rating — Accessibility Contract

- **Component:** Rating
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Rating.Semantic.md) · [Interaction](./Rating.Interaction.md) · [Styling](./Rating.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Rating.tsx`
- **Catalog row:** #110 Rating (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="radiogroup"` | Container `<div>` | Interactive mode (non-readonly) |
| `role="img"` | Container `<div>` | Readonly mode |
| `aria-label="Rating: {current} of {max}"` | Container `<div>` | Always; dynamic |
| `<button type="button">` | Each star | Keyboard focusable in interactive mode |
| `aria-label="{i} star(s)"` | Each star button | Names the star |
| `disabled` | Each star button | Boolean when `disabled=true` |

---

## 2. Mode-dependent roles

| Mode | Container role | Star elements |
|---|---|---|
| Interactive | `role="radiogroup"` | `<button>` (focusable, clickable) |
| Readonly | `role="img"` | `<button>` rendered but `onClick` absent; semantically acts as display |
| Disabled | `role="radiogroup"` | `<button disabled>` |

---

## 3. Container label

`aria-label="Rating: {current ?? 0} of {max}"` — updates reactively. AT announces this when focus enters the container.

---

## 4. Star labels

Each star: `aria-label="{i} star"` / `"{i} stars"` (plural for i > 1). AT reads: `"3 stars, button"` (unpressed) or `"3 stars, button"`.

*Note: `aria-pressed` is not applied to star buttons in M1 — current selected star is not indicated via AT other than the container `aria-label`.*

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RAT3 | Medium | Individual stars do not have `aria-pressed` — AT cannot determine which star is currently selected within the group | Accepted-risk M1; container `aria-label` partially compensates |
| G-RAT4 | Low | Readonly button elements are still `<button>` (focusable) rather than `<span aria-hidden>` — adds unnecessary Tab stops | Accepted-risk M1 |
